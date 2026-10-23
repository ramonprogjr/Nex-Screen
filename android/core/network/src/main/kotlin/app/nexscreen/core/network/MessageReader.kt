package app.nexscreen.core.network

import java.io.EOFException
import java.io.InputStream

/** A header with its payload, before type-specific parsing. */
class RawMessage(val header: Header, val payload: ByteArray)

/** Reads framed messages from a stream, tolerating arbitrarily split reads. */
class MessageReader(private val input: InputStream) {
    private val headerBytes = ByteArray(NexLink.HEADER_SIZE)

    /** Returns null on a clean end of stream between messages; throws [EOFException] mid-message. */
    fun read(): RawMessage? {
        if (!readFully(headerBytes, allowEofAtStart = true)) return null
        val header = Header.decode(headerBytes)
        val payload = ByteArray(header.length)
        readFully(payload, allowEofAtStart = false)
        return RawMessage(header, payload)
    }

    private fun readFully(target: ByteArray, allowEofAtStart: Boolean): Boolean {
        var offset = 0
        while (offset < target.size) {
            val n = input.read(target, offset, target.size - offset)
            if (n < 0) {
                if (offset == 0 && allowEofAtStart) return false
                throw EOFException("Stream ended after $offset of ${target.size} bytes")
            }
            offset += n
        }
        return true
    }
}
