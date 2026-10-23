package app.nexscreen.core.network

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** The fixed 16-byte big-endian header that precedes every message. */
data class Header(
    val type: Int,
    val flags: Int,
    val length: Int,
    val timestampUs: Long,
    val version: Int = NexLink.VERSION,
) {
    fun encode(): ByteArray {
        val buffer = ByteBuffer.allocate(NexLink.HEADER_SIZE).order(ByteOrder.BIG_ENDIAN)
        writeTo(buffer)
        return buffer.array()
    }

    fun writeTo(buffer: ByteBuffer) {
        buffer.put(version.toByte())
        buffer.put(type.toByte())
        buffer.putShort(flags.toShort())
        buffer.putInt(length)
        buffer.putLong(timestampUs)
    }

    companion object {
        /** Decodes and validates a header. Throws [ProtocolException] for anything the receiver must reject. */
        fun decode(bytes: ByteArray, offset: Int = 0): Header {
            require(bytes.size - offset >= NexLink.HEADER_SIZE) { "Header needs ${NexLink.HEADER_SIZE} bytes" }
            val buffer = ByteBuffer.wrap(bytes, offset, NexLink.HEADER_SIZE).order(ByteOrder.BIG_ENDIAN)
            val version = buffer.get().toInt() and 0xFF
            val type = buffer.get().toInt() and 0xFF
            val flags = buffer.getShort().toInt() and 0xFFFF
            val length = buffer.getInt().toLong() and 0xFFFF_FFFFL
            val timestampUs = buffer.getLong()
            if (version != NexLink.VERSION) {
                throw ProtocolException("Unsupported protocol version $version")
            }
            if (!MessageType.isKnown(type)) {
                throw ProtocolException("Unknown message type 0x%02x".format(type))
            }
            if (length > NexLink.MAX_PAYLOAD) {
                throw ProtocolException("Payload length $length exceeds ${NexLink.MAX_PAYLOAD}")
            }
            return Header(type, flags, length.toInt(), timestampUs, version)
        }
    }
}
