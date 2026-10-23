package app.nexscreen

import android.util.Log
import android.view.Surface
import app.nexscreen.core.network.ConnectionState
import app.nexscreen.core.network.Hello
import app.nexscreen.core.network.NexLinkSession
import app.nexscreen.core.network.ServerMessage
import app.nexscreen.core.network.Stats
import app.nexscreen.core.network.TcpTransport
import app.nexscreen.core.video.VideoDecoder
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class StreamUiState(
    val connection: ConnectionState = ConnectionState.Idle,
    val videoWidth: Int = 0,
    val videoHeight: Int = 0,
    val fps: Int = 0,
    /** True once a frame of the current session reached the screen. */
    val rendering: Boolean = false,
    val displayedFps: Float = 0f,
    val latencyP50Ms: Float = 0f,
    val latencyP95Ms: Float = 0f,
    val framesDropped: Long = 0,
    /** Set when the tablet cannot decode the stream the companion sent. */
    val decoderUnavailable: Boolean = false,
)

/** Glues the NexLink session to the decoder and exposes the state the UI shows. */
class StreamController(private val scope: CoroutineScope) {

    private val _ui = MutableStateFlow(StreamUiState())
    val ui: StateFlow<StreamUiState> = _ui.asStateFlow()

    /** Updated by the Activity on every configuration change; read when a connection starts. */
    @Volatile
    var hello: Hello = Hello(1920, 1200, 240, 0)

    private val decoder = VideoDecoder(object : VideoDecoder.Listener {
        override fun onKeyframeNeeded() = session.requestKeyframe()

        override fun onFirstFrameRendered() = _ui.update { it.copy(rendering = true, decoderUnavailable = false) }

        override fun onDecoderUnavailable(width: Int, height: Int) = _ui.update { it.copy(decoderUnavailable = true) }
    })

    private val session: NexLinkSession = NexLinkSession(
        transportFactory = { TcpTransport() },
        helloProvider = { hello },
        statsProvider = ::collectStats,
        listener = object : NexLinkSession.Listener {
            override fun onHelloAck(ack: ServerMessage.HelloAck) {
                Log.i(TAG, "HELLO_ACK ${ack.videoWidth}x${ack.videoHeight}@${ack.fps}")
                decoder.configure(ack.videoWidth, ack.videoHeight, ack.fps)
                _ui.update {
                    it.copy(
                        videoWidth = ack.videoWidth,
                        videoHeight = ack.videoHeight,
                        fps = ack.fps,
                        rendering = false,
                        decoderUnavailable = false,
                    )
                }
            }

            override fun onCodecConfig(config: ServerMessage.CodecConfig) {
                decoder.submitCodecConfig(config.data)
            }

            override fun onFrame(frame: ServerMessage.Frame) {
                // No cross-clock lateness check: the companion's timestamps are on its own clock and keep-alive
                // re-encodes are stamped differently from captured frames. Backlog is handled by decoder
                // backpressure here and by frame skipping on the companion.
                decoder.submitFrame(frame.data, frame.keyframe)
            }

            override fun onDisconnected() {
                decoder.resetCounters()
                _ui.update { it.copy(rendering = false, displayedFps = 0f, decoderUnavailable = false) }
            }
        },
    )

    private var job: Job? = null

    fun start() {
        if (job?.isActive == true) return
        job = scope.launch(Dispatchers.IO) {
            launch {
                session.state.collect { state ->
                    Log.i(TAG, "Connection: $state")
                    _ui.update { it.copy(connection = state) }
                }
            }
            session.run()
        }
    }

    fun stop() {
        job?.cancel()
        job = null
    }

    /** Called on the main thread from the SurfaceHolder callbacks. */
    fun setSurface(surface: Surface?) = decoder.setSurface(surface)

    fun release() {
        stop()
        decoder.release()
    }

    private fun collectStats(): Stats {
        val s = decoder.snapshotStats()
        val displayed = if (s.intervalMs > 0) s.renderedInInterval * 1000f / s.intervalMs else 0f
        _ui.update {
            it.copy(
                displayedFps = displayed,
                latencyP50Ms = s.latencyUsP50 / 1000f,
                latencyP95Ms = s.latencyUsP95 / 1000f,
                framesDropped = s.dropped,
            )
        }
        return Stats(s.decoded, s.dropped, s.latencyUsP50, s.latencyUsP95)
    }

    private companion object {
        const val TAG = "NexScreen"
    }
}
