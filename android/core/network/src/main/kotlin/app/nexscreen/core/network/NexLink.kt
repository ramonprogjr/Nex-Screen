package app.nexscreen.core.network

import java.io.IOException

/** Constants of NexLink v0-usb (protocol/nexlink-v0-usb.md). */
object NexLink {
    const val VERSION = 0
    const val HEADER_SIZE = 16
    const val MAX_PAYLOAD = 16 * 1024 * 1024
    const val DEFAULT_HOST = "127.0.0.1"
    const val DEFAULT_PORT = 27183
    const val CODEC_H264 = 1
    const val FLAG_KEYFRAME = 0x0001

    internal val MAGIC = "NXS0".toByteArray(Charsets.US_ASCII)
}

object MessageType {
    const val HELLO = 0x01
    const val REQUEST_KEYFRAME = 0x10
    const val STATS = 0x11
    const val POINTER = 0x20
    const val SCROLL = 0x21
    const val HELLO_ACK = 0x81
    const val CODEC_CONFIG = 0x82
    const val FRAME = 0x83

    fun isKnown(type: Int): Boolean = when (type) {
        HELLO, REQUEST_KEYFRAME, STATS, POINTER, SCROLL, HELLO_ACK, CODEC_CONFIG, FRAME -> true
        else -> false
    }
}

/** Any violation of the wire format. The connection must be closed when this is thrown. */
class ProtocolException(message: String) : IOException(message)

/** Monotonic clock in microseconds, used for header timestamps. */
fun interface MonotonicClock {
    fun nowUs(): Long

    companion object {
        val SYSTEM = MonotonicClock { System.nanoTime() / 1_000 }
    }
}
