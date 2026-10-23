package app.nexscreen.core.video

import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.os.Build
import android.util.Log

/** A decoder candidate for H.264 and whether it advertises low-latency decoding. */
data class DecoderChoice(val name: String, val lowLatency: Boolean, val hardware: Boolean)

/**
 * Picks the H.264 decoder: low-latency first (FEATURE_LowLatency or a dedicated ".low_latency" codec, which
 * Qualcomm ships as c2.qti.avc.decoder.low_latency), then hardware over software. Secure-only codecs and
 * aliases are skipped.
 */
object DecoderSelector {
    const val MIME = "video/avc"
    private const val TAG = "NexScreenDecoder"

    fun candidates(width: Int, height: Int): List<DecoderChoice> {
        val infos = try {
            MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos
        } catch (e: RuntimeException) {
            Log.w(TAG, "MediaCodecList failed", e)
            return emptyList()
        }
        val choices = mutableListOf<DecoderChoice>()
        for (info in infos) {
            if (info.isEncoder || info.supportedTypes.none { it.equals(MIME, ignoreCase = true) }) continue
            val skip = skipReason(info, width, height)
            if (skip != null) {
                Log.d(TAG, "Skipping ${info.name}: $skip")
                continue
            }
            val caps = info.getCapabilitiesForType(MIME)
            val lowLatency = info.name.endsWith(".low_latency") ||
                (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
                    caps.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency))
            choices += DecoderChoice(info.name, lowLatency, isHardware(info))
        }
        return choices.sortedWith(
            compareByDescending<DecoderChoice> { it.lowLatency && it.hardware }
                .thenByDescending { it.hardware }
                .thenByDescending { it.lowLatency },
        )
    }

    private fun skipReason(info: MediaCodecInfo, width: Int, height: Int): String? {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && info.isAlias) return "alias"
        if (info.name.endsWith(".secure")) return "secure"
        val caps = try {
            info.getCapabilitiesForType(MIME)
        } catch (e: IllegalArgumentException) {
            return "no capabilities (${e.message})"
        }
        if (caps.isFeatureRequired(MediaCodecInfo.CodecCapabilities.FEATURE_SecurePlayback)) return "secure only"
        val video = caps.videoCapabilities ?: return null
        // Some decoders list only one orientation of their maximum size, so accept either
        if (!video.isSizeSupported(width, height) && !video.isSizeSupported(height, width)) {
            return "size ${width}x$height unsupported (max ${video.supportedWidths.upper}x${video.supportedHeights.upper})"
        }
        return null
    }

    private fun isHardware(info: MediaCodecInfo): Boolean {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) return info.isHardwareAccelerated
        val name = info.name.lowercase()
        return !(name.startsWith("omx.google.") || name.startsWith("c2.android.") || name.contains(".sw."))
    }
}
