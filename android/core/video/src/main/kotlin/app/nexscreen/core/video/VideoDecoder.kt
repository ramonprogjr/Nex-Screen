package app.nexscreen.core.video

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.SystemClock
import android.util.Log
import android.view.Surface
import java.util.concurrent.atomic.AtomicLong

/** Decoder counters. [decoded] and [dropped] are cumulative; the rest cover the interval since the last snapshot. */
data class DecoderStats(
    val decoded: Long,
    val dropped: Long,
    val latencyUsP50: Long,
    val latencyUsP95: Long,
    val renderedInInterval: Int,
    val intervalMs: Long,
    val decoderName: String?,
)

/**
 * H.264 decoder that renders straight into a [Surface] (no CPU copies).
 *
 * Input runs on the caller's thread (the network receive loop) in synchronous mode; a dedicated thread drains
 * output and renders each frame as soon as it is ready, skipping older frames when several are pending.
 * Instead of letting a backlog build, frames that cannot be queued promptly are dropped and the decoder waits
 * for the next keyframe, asking for one through [Listener.onKeyframeNeeded].
 */
class VideoDecoder(private val listener: Listener) {

    interface Listener {
        /** Called from the input thread; the caller should send REQUEST_KEYFRAME (rate-limited). */
        fun onKeyframeNeeded()

        /** The first frame of a (re)configured stream reached the screen. Called from the output thread. */
        fun onFirstFrameRendered() {}

        /** No decoder on this device accepts the stream (e.g. the size exceeds the hardware limit). */
        fun onDecoderUnavailable(width: Int, height: Int) {}
    }

    private data class StreamFormat(val width: Int, val height: Int, val fps: Int)

    private val lock = Any()
    private var surface: Surface? = null
    private var format: StreamFormat? = null
    private var codecConfig: ByteArray? = null
    private var codec: MediaCodec? = null
    private var codecName: String? = null
    private var output: OutputLoop? = null
    private var configQueued = false
    private var waitingForKeyframe = true
    private var nextPtsUs = 0L

    @Volatile
    private var codecFailed = false

    private val decoded = AtomicLong()
    private val dropped = AtomicLong()
    private val latency = LatencyWindow()
    private val queueTimesNs = LongArray(PENDING_SLOTS)
    private var lastSnapshotMs = SystemClock.elapsedRealtime()

    /** Sets or clears the output surface. Clearing it releases the codec; setting it rebuilds it if possible. */
    fun setSurface(newSurface: Surface?) = synchronized(lock) {
        if (newSurface === surface) return@synchronized
        releaseCodecLocked()
        surface = newSurface
        if (newSurface != null && startCodecLocked()) listener.onKeyframeNeeded()
    }

    /** HELLO_ACK: a new stream format. The codec is rebuilt when the matching CODEC_CONFIG arrives. */
    fun configure(width: Int, height: Int, fps: Int) = synchronized(lock) {
        releaseCodecLocked()
        format = StreamFormat(width, height, fps)
        codecConfig = null
    }

    /** CODEC_CONFIG: SPS + PPS in Annex-B. */
    fun submitCodecConfig(data: ByteArray) = synchronized(lock) {
        if (codec != null && configQueued && data.contentEquals(codecConfig)) return@synchronized
        releaseCodecLocked()
        codecConfig = data
        startCodecLocked()
    }

    /** Asks the decoder to skip frames until the next keyframe (e.g. the network fell behind). */
    fun dropUntilKeyframe() = synchronized(lock) {
        waitingForKeyframe = true
    }

    /** FRAME: one access unit in Annex-B. */
    fun submitFrame(data: ByteArray, keyframe: Boolean) = synchronized(lock) {
        if (codecFailed) {
            Log.w(TAG, "Recovering from decoder error")
            releaseCodecLocked()
            if (startCodecLocked()) listener.onKeyframeNeeded()
        }
        val c = codec
        if (c == null || !configQueued) {
            // No surface yet or waiting for CODEC_CONFIG; a keyframe is requested once the codec starts
            dropped.incrementAndGet()
            waitingForKeyframe = true
            return@synchronized
        }
        if (!keyframe && waitingForKeyframe) {
            dropped.incrementAndGet()
            return@synchronized
        }
        try {
            // A lost keyframe costs a round trip for a new one, so wait longer for a buffer (right after start()
            // the decoder may still hold its buffers while it processes the codec config)
            val timeoutUs = if (keyframe) KEYFRAME_INPUT_TIMEOUT_US else INPUT_TIMEOUT_US
            val index = c.dequeueInputBuffer(timeoutUs)
            if (index < 0) {
                dropFrameLocked("no input buffer within ${timeoutUs / 1000} ms")
                return@synchronized
            }
            val buffer = c.getInputBuffer(index) ?: throw IllegalStateException("null input buffer")
            if (data.size > buffer.capacity()) {
                c.queueInputBuffer(index, 0, 0, 0, 0)
                dropFrameLocked("frame of ${data.size} bytes exceeds input buffer of ${buffer.capacity()}")
                return@synchronized
            }
            buffer.clear()
            buffer.put(data)
            val pts = nextPtsUs
            nextPtsUs += PTS_STEP_US
            queueTimesNs[slot(pts)] = System.nanoTime()
            c.queueInputBuffer(index, 0, data.size, pts, 0)
            if (keyframe) waitingForKeyframe = false
        } catch (e: IllegalStateException) {
            // MediaCodec.CodecException is an IllegalStateException
            Log.e(TAG, "queueInputBuffer failed", e)
            codecFailed = true
            dropped.incrementAndGet()
            waitingForKeyframe = true
            listener.onKeyframeNeeded()
        }
    }

    fun snapshotStats(): DecoderStats {
        val now = SystemClock.elapsedRealtime()
        val window = latency.drain()
        val interval = now - lastSnapshotMs
        lastSnapshotMs = now
        return DecoderStats(
            decoded = decoded.get(),
            dropped = dropped.get(),
            latencyUsP50 = window.p50Us,
            latencyUsP95 = window.p95Us,
            renderedInInterval = window.count,
            intervalMs = interval,
            decoderName = synchronized(lock) { codecName },
        )
    }

    /** Resets the cumulative counters, e.g. at the start of a new session. */
    fun resetCounters() {
        decoded.set(0)
        dropped.set(0)
        latency.drain()
    }

    fun release() = synchronized(lock) {
        releaseCodecLocked()
        surface = null
        format = null
        codecConfig = null
    }

    private fun dropFrameLocked(reason: String) {
        Log.w(TAG, "Dropping frame: $reason")
        dropped.incrementAndGet()
        waitingForKeyframe = true
        listener.onKeyframeNeeded()
    }

    /** Creates, configures and starts the codec and queues the codec config. Returns false if not possible yet. */
    private fun startCodecLocked(): Boolean {
        val s = surface ?: return false
        val f = format ?: return false
        val config = codecConfig ?: return false
        if (!s.isValid) return false

        val choices = DecoderSelector.candidates(f.width, f.height)
        Log.i(TAG, "AVC decoders: ${choices.joinToString { "${it.name}(ll=${it.lowLatency},hw=${it.hardware})" }}")
        // Try each decoder with the latency tuning first, then without it; createDecoderByType is the last resort
        val attempts = choices.flatMap { listOf(it.name to true, it.name to false) } + (null to false)
        for ((name, tuned) in attempts) {
            val c = try {
                if (name != null) MediaCodec.createByCodecName(name) else MediaCodec.createDecoderByType(DecoderSelector.MIME)
            } catch (e: Exception) {
                Log.w(TAG, "Cannot create ${name ?: "default decoder"}", e)
                continue
            }
            try {
                c.configure(buildFormat(f, c.name, tuned), s, null, 0)
                c.start()
                val index = c.dequeueInputBuffer(CONFIG_TIMEOUT_US)
                if (index < 0) throw IllegalStateException("no input buffer for codec config")
                val buffer = c.getInputBuffer(index) ?: throw IllegalStateException("null input buffer")
                buffer.clear()
                buffer.put(config)
                c.queueInputBuffer(index, 0, config.size, 0, MediaCodec.BUFFER_FLAG_CODEC_CONFIG)
            } catch (e: Exception) {
                // CodecException, IllegalStateException or IllegalArgumentException from configure/start
                Log.w(TAG, "Decoder ${c.name} failed to start (tuned=$tuned)", e)
                c.release()
                continue
            }
            Log.i(TAG, "Using ${c.name} for ${f.width}x${f.height}@${f.fps} (tuned=$tuned)")
            codec = c
            codecName = c.name
            configQueued = true
            codecFailed = false
            waitingForKeyframe = true
            nextPtsUs = PTS_STEP_US
            output = OutputLoop(c).also { it.start() }
            return true
        }
        Log.e(TAG, "No usable H.264 decoder for ${f.width}x${f.height}")
        listener.onDecoderUnavailable(f.width, f.height)
        return false
    }

    private fun buildFormat(f: StreamFormat, name: String, tuned: Boolean): MediaFormat =
        MediaFormat.createVideoFormat(DecoderSelector.MIME, f.width, f.height).apply {
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(f.width * f.height * 3 / 2, 1 shl 20))
            // Without these, c2.qti.avc.decoder assumes a 1920x1079 maximum and fails start() for 1920x1200
            setInteger(MediaFormat.KEY_MAX_WIDTH, f.width)
            setInteger(MediaFormat.KEY_MAX_HEIGHT, f.height)
            if (!tuned) return@apply
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }
            setInteger(MediaFormat.KEY_PRIORITY, 0) // realtime
            if (f.fps > 0) setFloat(MediaFormat.KEY_OPERATING_RATE, f.fps.toFloat())
            if (name.startsWith("c2.qti.") || name.startsWith("OMX.qcom.")) {
                // Qualcomm vendor extension, honored by older firmware that ignores KEY_LOW_LATENCY
                setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
            }
        }

    private fun releaseCodecLocked() {
        output?.stopAndJoin()
        output = null
        codec?.let {
            try {
                it.stop()
            } catch (_: IllegalStateException) {
            }
            it.release()
        }
        codec = null
        codecName = null
        configQueued = false
        codecFailed = false
        waitingForKeyframe = true
    }

    private fun slot(pts: Long) = ((pts / PTS_STEP_US) % PENDING_SLOTS).toInt()

    private inner class OutputLoop(private val c: MediaCodec) : Thread("nexscreen-decoder-out") {
        @Volatile
        private var running = true
        private var renderedFirst = false

        fun stopAndJoin() {
            running = false
            join(1_000)
        }

        override fun run() {
            val info = MediaCodec.BufferInfo()
            try {
                while (running) {
                    var index = c.dequeueOutputBuffer(info, OUTPUT_TIMEOUT_US)
                    if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        Log.i(TAG, "Output format: ${c.outputFormat}")
                        continue
                    }
                    if (index < 0) continue
                    // If more frames are already decoded, show only the newest
                    while (true) {
                        recordDecoded(info.presentationTimeUs)
                        val next = c.dequeueOutputBuffer(info, 0)
                        if (next < 0) break
                        c.releaseOutputBuffer(index, false)
                        dropped.incrementAndGet()
                        index = next
                    }
                    c.releaseOutputBuffer(index, true)
                    if (!renderedFirst) {
                        renderedFirst = true
                        listener.onFirstFrameRendered()
                    }
                }
            } catch (e: IllegalStateException) {
                if (running) {
                    Log.e(TAG, "Decoder output failed", e)
                    codecFailed = true
                }
            }
        }

        private fun recordDecoded(pts: Long) {
            decoded.incrementAndGet()
            if (pts <= 0) return
            val queuedAt = queueTimesNs[slot(pts)]
            if (queuedAt != 0L) latency.add((System.nanoTime() - queuedAt) / 1_000)
        }
    }

    private companion object {
        const val TAG = "NexScreenDecoder"
        const val INPUT_TIMEOUT_US = 20_000L
        const val KEYFRAME_INPUT_TIMEOUT_US = 250_000L
        const val CONFIG_TIMEOUT_US = 500_000L
        const val OUTPUT_TIMEOUT_US = 10_000L
        const val PTS_STEP_US = 1_000L
        const val PENDING_SLOTS = 128
    }
}

/** Decode latency samples (queue to output) for one stats interval. */
internal class LatencyWindow {
    class Summary(val count: Int, val p50Us: Long, val p95Us: Long)

    private val samples = LongArray(1024)
    private var count = 0

    @Synchronized
    fun add(us: Long) {
        if (count < samples.size) samples[count] = us
        count++
    }

    @Synchronized
    fun drain(): Summary {
        val n = minOf(count, samples.size)
        val total = count
        count = 0
        if (n == 0) return Summary(total, 0, 0)
        val sorted = samples.copyOf(n).also { it.sort() }
        return Summary(total, sorted[(n - 1) * 50 / 100], sorted[(n - 1) * 95 / 100])
    }
}
