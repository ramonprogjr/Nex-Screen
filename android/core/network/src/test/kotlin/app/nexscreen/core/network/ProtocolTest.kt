package app.nexscreen.core.network

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.InputStream

class ProtocolTest {

    @Test
    fun headerRoundTrip() {
        val header = Header(type = MessageType.FRAME, flags = 0xBEEF, length = 123_456, timestampUs = 0x0102030405060708L)
        val bytes = header.encode()
        assertEquals(NexLink.HEADER_SIZE, bytes.size)
        assertEquals(header, Header.decode(bytes))
    }

    @Test
    fun headerIsBigEndian() {
        val bytes = Header(type = MessageType.HELLO_ACK, flags = 1, length = 0x00010203, timestampUs = 0x0A0B0C0D0E0F1011L).encode()
        val expected = byteArrayOf(
            0x00, 0x81.toByte(), 0x00, 0x01,
            0x00, 0x01, 0x02, 0x03,
            0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11,
        )
        assertArrayEquals(expected, bytes)
    }

    @Test
    fun helloPayloadLayout() {
        val hello = Hello(screenWidth = 1920, screenHeight = 1200, densityDpi = 240, rotation = 90)
        val message = Messages.hello(hello, timestampUs = 42)
        assertEquals(NexLink.HEADER_SIZE + Hello.PAYLOAD_SIZE, message.size)

        val header = Header.decode(message)
        assertEquals(MessageType.HELLO, header.type)
        assertEquals(Hello.PAYLOAD_SIZE, header.length)
        assertEquals(42L, header.timestampUs)

        val payload = message.copyOfRange(NexLink.HEADER_SIZE, message.size)
        val expected = byteArrayOf(
            'N'.code.toByte(), 'X'.code.toByte(), 'S'.code.toByte(), '0'.code.toByte(),
            0x07, 0x80.toByte(), // 1920
            0x04, 0xB0.toByte(), // 1200
            0x00, 0xF0.toByte(), // 240
            0x00, 0x5A, // 90
        )
        assertArrayEquals(expected, payload)
        assertEquals(hello, Hello.decodePayload(payload))
    }

    @Test(expected = IllegalArgumentException::class)
    fun helloRejectsInvalidRotation() {
        Hello(1920, 1200, 240, 45)
    }

    @Test
    fun statsPayloadRoundTrip() {
        val stats = Stats(framesDecoded = 1000, framesDropped = 3, decodeLatencyUsP50 = 4_200, decodeLatencyUsP95 = 9_900)
        val message = Messages.stats(stats, timestampUs = 1)
        val header = Header.decode(message)
        assertEquals(MessageType.STATS, header.type)
        assertEquals(stats, Stats.decodePayload(message.copyOfRange(NexLink.HEADER_SIZE, message.size)))
    }

    @Test
    fun requestKeyframeHasEmptyPayload() {
        val message = Messages.requestKeyframe(timestampUs = 7)
        assertEquals(NexLink.HEADER_SIZE, message.size)
        val header = Header.decode(message)
        assertEquals(MessageType.REQUEST_KEYFRAME, header.type)
        assertEquals(0, header.length)
    }

    @Test
    fun rejectsBadVersion() {
        val bytes = Header(type = MessageType.FRAME, flags = 0, length = 0, timestampUs = 0, version = 1).encode()
        assertProtocolError { Header.decode(bytes) }
    }

    @Test
    fun rejectsUnknownType() {
        val bytes = Header(type = 0x7F, flags = 0, length = 0, timestampUs = 0).encode()
        assertProtocolError { Header.decode(bytes) }
    }

    @Test
    fun rejectsOversizedLength() {
        val atLimit = Header(type = MessageType.FRAME, flags = 0, length = NexLink.MAX_PAYLOAD, timestampUs = 0).encode()
        assertEquals(NexLink.MAX_PAYLOAD, Header.decode(atLimit).length)

        val over = Header(type = MessageType.FRAME, flags = 0, length = NexLink.MAX_PAYLOAD + 1, timestampUs = 0).encode()
        assertProtocolError { Header.decode(over) }

        // A length with the top bit set must be read as a large u32, not a negative int
        val huge = Header(type = MessageType.FRAME, flags = 0, length = -1, timestampUs = 0).encode()
        assertProtocolError { Header.decode(huge) }
    }

    @Test
    fun readerRejectsOversizedLengthBeforeAllocating() {
        val bytes = Header(type = MessageType.FRAME, flags = 0, length = Int.MAX_VALUE, timestampUs = 0).encode()
        assertProtocolError { MessageReader(ByteArrayInputStream(bytes)).read() }
    }

    @Test
    fun parsesServerMessages() {
        val stream = ByteArrayOutputStream()
        val ack = ServerMessage.HelloAck(1920, 1200, 30, NexLink.CODEC_H264, timestampUs = 10)
        stream.write(Messages.encode(MessageType.HELLO_ACK, ack.encodePayload(), timestampUs = 10))
        val sps = byteArrayOf(0, 0, 0, 1, 0x67, 0x64, 0, 0x28, 0, 0, 0, 1, 0x68, 0xEE.toByte(), 0x3C, 0x80.toByte())
        stream.write(Messages.encode(MessageType.CODEC_CONFIG, sps, timestampUs = 11))
        val idr = byteArrayOf(0, 0, 0, 1, 0x65, 1, 2, 3)
        stream.write(Messages.encode(MessageType.FRAME, idr, flags = NexLink.FLAG_KEYFRAME, timestampUs = 12))
        val p = byteArrayOf(0, 0, 0, 1, 0x41, 9)
        stream.write(Messages.encode(MessageType.FRAME, p, timestampUs = 13))

        val reader = MessageReader(ByteArrayInputStream(stream.toByteArray()))
        val parsed = generateSequence { reader.read() }.map { Messages.parseServerMessage(it.header, it.payload) }.toList()

        assertEquals(4, parsed.size)
        assertEquals(ack, parsed[0])
        assertArrayEquals(sps, (parsed[1] as ServerMessage.CodecConfig).data)
        val keyframe = parsed[2] as ServerMessage.Frame
        assertTrue(keyframe.keyframe)
        assertArrayEquals(idr, keyframe.data)
        assertEquals(12L, keyframe.timestampUs)
        val delta = parsed[3] as ServerMessage.Frame
        assertFalse(delta.keyframe)
        assertArrayEquals(p, delta.data)
    }

    @Test
    fun rejectsBadHelloAck() {
        val badMagic = ServerMessage.HelloAck(1920, 1200, 30, NexLink.CODEC_H264, 0).encodePayload().also { it[0] = 'X'.code.toByte() }
        assertProtocolError { parse(MessageType.HELLO_ACK, badMagic) }

        val badCodec = ServerMessage.HelloAck(1920, 1200, 30, 2, 0).encodePayload()
        assertProtocolError { parse(MessageType.HELLO_ACK, badCodec) }

        assertProtocolError { parse(MessageType.HELLO_ACK, ByteArray(11)) }
    }

    @Test
    fun rejectsClientMessagesFromServer() {
        assertProtocolError { parse(MessageType.STATS, Stats.EMPTY.encodePayload()) }
    }

    @Test
    fun partialReads() {
        val frame = ByteArray(70_000) { it.toByte() }
        val bytes = Messages.encode(MessageType.FRAME, frame, flags = NexLink.FLAG_KEYFRAME, timestampUs = 99) +
            Messages.encode(MessageType.CODEC_CONFIG, byteArrayOf(1, 2, 3), timestampUs = 100)

        for (chunk in listOf(1, 3, 7, 16, 17, 4096)) {
            val reader = MessageReader(ChunkedInputStream(bytes, chunk))
            val first = reader.read()!!
            assertEquals(MessageType.FRAME, first.header.type)
            assertEquals(NexLink.FLAG_KEYFRAME, first.header.flags)
            assertArrayEquals(frame, first.payload)
            val second = reader.read()!!
            assertArrayEquals(byteArrayOf(1, 2, 3), second.payload)
            assertNull(reader.read())
        }
    }

    @Test
    fun truncatedStreamIsAnError() {
        val bytes = Messages.encode(MessageType.FRAME, ByteArray(100), timestampUs = 0)
        for (cut in listOf(5, NexLink.HEADER_SIZE, NexLink.HEADER_SIZE + 50)) {
            val reader = MessageReader(ByteArrayInputStream(bytes.copyOf(cut)))
            try {
                reader.read()
                fail("expected EOFException for cut at $cut")
            } catch (_: EOFException) {
            }
        }
    }

    @Test
    fun pointerPayloadLayout() {
        val event = PointerEvent(PointerEvent.ACTION_MOVE, pointerId = 3, buttons = PointerEvent.BUTTON_LEFT, xNorm = 0x8000, yNorm = 0xFFFF)
        val message = Messages.pointer(event, timestampUs = 5)
        assertEquals(NexLink.HEADER_SIZE + PointerEvent.PAYLOAD_SIZE, message.size)
        val header = Header.decode(message)
        assertEquals(MessageType.POINTER, header.type)
        assertEquals(PointerEvent.PAYLOAD_SIZE, header.length)
        assertEquals(5L, header.timestampUs)
        val payload = message.copyOfRange(NexLink.HEADER_SIZE, message.size)
        assertArrayEquals(byteArrayOf(0x01, 0x03, 0x00, 0x01, 0x80.toByte(), 0x00, 0xFF.toByte(), 0xFF.toByte()), payload)
        assertEquals(event, PointerEvent.decodePayload(payload))
    }

    @Test
    fun pointerValidation() {
        assertProtocolError { PointerEvent.decodePayload(byteArrayOf(4, 0, 0, 0, 0, 0, 0, 0)) } // action out of range
        assertProtocolError { PointerEvent.decodePayload(byteArrayOf(0, 0, 0, 8, 0, 0, 0, 0)) } // unknown button bit
        assertProtocolError { PointerEvent.decodePayload(ByteArray(7)) }
        assertProtocolError { PointerEvent.decodePayload(ByteArray(9)) }
        for (bad in listOf({ PointerEvent(4, 0, 0, 0, 0) }, { PointerEvent(0, 256, 0, 0, 0) },
            { PointerEvent(0, 0, 8, 0, 0) }, { PointerEvent(0, 0, 0, 65_536, 0) }, { PointerEvent(0, 0, 0, 0, -1) })) {
            try {
                bad()
                fail("expected IllegalArgumentException")
            } catch (_: IllegalArgumentException) {
            }
        }
    }

    @Test
    fun scrollPayloadIsSignedBigEndian() {
        val event = ScrollEvent(dx = -120, dy = 240)
        val message = Messages.scroll(event, timestampUs = 9)
        assertEquals(MessageType.SCROLL, Header.decode(message).type)
        val payload = message.copyOfRange(NexLink.HEADER_SIZE, message.size)
        assertArrayEquals(byteArrayOf(0xFF.toByte(), 0x88.toByte(), 0x00, 0xF0.toByte()), payload)
        assertEquals(event, ScrollEvent.decodePayload(payload))
        assertProtocolError { ScrollEvent.decodePayload(ByteArray(5)) }
        assertEquals(ScrollEvent(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt()),
            ScrollEvent.decodePayload(byteArrayOf(0x80.toByte(), 0, 0x7F, 0xFF.toByte())))
    }

    @Test
    fun rejectsInputMessagesFromServer() {
        assertProtocolError { parse(MessageType.POINTER, PointerEvent(0, 0, 1, 0, 0).encodePayload()) }
        assertProtocolError { parse(MessageType.SCROLL, ScrollEvent(0, 120).encodePayload()) }
    }

    private fun parse(type: Int, payload: ByteArray) =
        Messages.parseServerMessage(Header(type, 0, payload.size, 0), payload)

    private fun assertProtocolError(block: () -> Unit) {
        try {
            block()
            fail("expected ProtocolException")
        } catch (_: ProtocolException) {
        }
    }

    /** Returns at most [chunk] bytes per read call. */
    private class ChunkedInputStream(private val data: ByteArray, private val chunk: Int) : InputStream() {
        private var position = 0

        override fun read(): Int = if (position < data.size) data[position++].toInt() and 0xFF else -1

        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (position >= data.size) return -1
            val n = minOf(len, chunk, data.size - position)
            System.arraycopy(data, position, b, off, n)
            position += n
            return n
        }
    }
}
