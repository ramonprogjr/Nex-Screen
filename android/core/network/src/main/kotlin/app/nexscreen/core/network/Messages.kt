package app.nexscreen.core.network

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** HELLO payload: the tablet's current screen. Rotation is in degrees (0/90/180/270). */
data class Hello(
    val screenWidth: Int,
    val screenHeight: Int,
    val densityDpi: Int,
    val rotation: Int,
) {
    init {
        requireU16(screenWidth, "screenWidth")
        requireU16(screenHeight, "screenHeight")
        requireU16(densityDpi, "densityDpi")
        require(rotation in VALID_ROTATIONS) { "rotation must be 0, 90, 180 or 270, was $rotation" }
    }

    fun encodePayload(): ByteArray = ByteBuffer.allocate(PAYLOAD_SIZE).order(ByteOrder.BIG_ENDIAN)
        .put(NexLink.MAGIC)
        .putShort(screenWidth.toShort())
        .putShort(screenHeight.toShort())
        .putShort(densityDpi.toShort())
        .putShort(rotation.toShort())
        .array()

    companion object {
        const val PAYLOAD_SIZE = 12
        private val VALID_ROTATIONS = setOf(0, 90, 180, 270)

        fun decodePayload(payload: ByteArray): Hello {
            if (payload.size != PAYLOAD_SIZE) throw ProtocolException("HELLO payload must be $PAYLOAD_SIZE bytes")
            val buffer = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
            checkMagic(buffer, "HELLO")
            return Hello(buffer.u16(), buffer.u16(), buffer.u16(), buffer.u16())
        }
    }
}

/** STATS payload. Counters are cumulative for the session; latencies cover the last interval. */
data class Stats(
    val framesDecoded: Long,
    val framesDropped: Long,
    val decodeLatencyUsP50: Long,
    val decodeLatencyUsP95: Long,
) {
    fun encodePayload(): ByteArray = ByteBuffer.allocate(PAYLOAD_SIZE).order(ByteOrder.BIG_ENDIAN)
        .putInt(toU32(framesDecoded))
        .putInt(toU32(framesDropped))
        .putInt(toU32(decodeLatencyUsP50))
        .putInt(toU32(decodeLatencyUsP95))
        .array()

    companion object {
        const val PAYLOAD_SIZE = 16
        val EMPTY = Stats(0, 0, 0, 0)

        fun decodePayload(payload: ByteArray): Stats {
            if (payload.size != PAYLOAD_SIZE) throw ProtocolException("STATS payload must be $PAYLOAD_SIZE bytes")
            val buffer = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
            return Stats(buffer.u32(), buffer.u32(), buffer.u32(), buffer.u32())
        }

        private fun toU32(value: Long): Int = value.coerceIn(0L, 0xFFFF_FFFFL).toInt()
    }
}

/** Input the tablet forwards to the companion (POINTER and SCROLL). */
sealed interface InputMessage

/**
 * POINTER payload. Coordinates are normalized to 0..65535 across the displayed video (letterbox bars excluded),
 * 0,0 being the top-left of the video. [buttons] is the button state after this event.
 */
data class PointerEvent(
    val action: Int,
    val pointerId: Int,
    val buttons: Int,
    val xNorm: Int,
    val yNorm: Int,
) : InputMessage {
    init {
        require(action in ACTION_DOWN..ACTION_CANCEL) { "action must be 0..3, was $action" }
        require(pointerId in 0..0xFF) { "pointerId out of u8 range: $pointerId" }
        require(buttons and BUTTON_MASK.inv() == 0) { "unknown button bits in $buttons" }
        requireU16(xNorm, "xNorm")
        requireU16(yNorm, "yNorm")
    }

    fun encodePayload(): ByteArray = ByteBuffer.allocate(PAYLOAD_SIZE).order(ByteOrder.BIG_ENDIAN)
        .put(action.toByte())
        .put(pointerId.toByte())
        .putShort(buttons.toShort())
        .putShort(xNorm.toShort())
        .putShort(yNorm.toShort())
        .array()

    companion object {
        const val PAYLOAD_SIZE = 8
        const val ACTION_DOWN = 0
        const val ACTION_MOVE = 1
        const val ACTION_UP = 2
        const val ACTION_CANCEL = 3
        const val BUTTON_LEFT = 0x1
        const val BUTTON_RIGHT = 0x2
        const val BUTTON_MIDDLE = 0x4
        const val BUTTON_MASK = BUTTON_LEFT or BUTTON_RIGHT or BUTTON_MIDDLE
        const val MAX_NORM = 0xFFFF

        fun decodePayload(payload: ByteArray): PointerEvent {
            if (payload.size != PAYLOAD_SIZE) throw ProtocolException("POINTER payload must be $PAYLOAD_SIZE bytes")
            val buffer = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
            val action = buffer.get().toInt() and 0xFF
            val id = buffer.get().toInt() and 0xFF
            val buttons = buffer.u16()
            if (action > ACTION_CANCEL) throw ProtocolException("Invalid POINTER action $action")
            if (buttons and BUTTON_MASK.inv() != 0) throw ProtocolException("Invalid POINTER buttons $buttons")
            return PointerEvent(action, id, buttons, buffer.u16(), buffer.u16())
        }
    }
}

/** SCROLL payload in wheel units (120 = one notch). Positive [dy] scrolls up, positive [dx] scrolls right. */
data class ScrollEvent(val dx: Int, val dy: Int) : InputMessage {
    init {
        require(dx in Short.MIN_VALUE..Short.MAX_VALUE) { "dx out of i16 range: $dx" }
        require(dy in Short.MIN_VALUE..Short.MAX_VALUE) { "dy out of i16 range: $dy" }
    }

    fun encodePayload(): ByteArray = ByteBuffer.allocate(PAYLOAD_SIZE).order(ByteOrder.BIG_ENDIAN)
        .putShort(dx.toShort())
        .putShort(dy.toShort())
        .array()

    companion object {
        const val PAYLOAD_SIZE = 4
        const val WHEEL_DELTA = 120

        fun decodePayload(payload: ByteArray): ScrollEvent {
            if (payload.size != PAYLOAD_SIZE) throw ProtocolException("SCROLL payload must be $PAYLOAD_SIZE bytes")
            val buffer = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
            return ScrollEvent(buffer.getShort().toInt(), buffer.getShort().toInt())
        }
    }
}

/** Messages the companion sends to the tablet. */
sealed interface ServerMessage {
    val timestampUs: Long

    data class HelloAck(
        val videoWidth: Int,
        val videoHeight: Int,
        val fps: Int,
        val codec: Int,
        override val timestampUs: Long,
    ) : ServerMessage {
        fun encodePayload(): ByteArray = ByteBuffer.allocate(PAYLOAD_SIZE).order(ByteOrder.BIG_ENDIAN)
            .put(NexLink.MAGIC)
            .putShort(videoWidth.toShort())
            .putShort(videoHeight.toShort())
            .putShort(fps.toShort())
            .put(codec.toByte())
            .put(0)
            .array()

        companion object {
            const val PAYLOAD_SIZE = 12
        }
    }

    /** SPS + PPS in Annex-B. */
    class CodecConfig(val data: ByteArray, override val timestampUs: Long) : ServerMessage

    /** One H.264 access unit in Annex-B. The timestamp is the capture time on the companion's clock. */
    class Frame(val data: ByteArray, val keyframe: Boolean, override val timestampUs: Long) : ServerMessage
}

object Messages {
    private val EMPTY = ByteArray(0)

    fun encode(type: Int, payload: ByteArray = EMPTY, flags: Int = 0, timestampUs: Long): ByteArray {
        require(payload.size <= NexLink.MAX_PAYLOAD) { "Payload too large: ${payload.size}" }
        val buffer = ByteBuffer.allocate(NexLink.HEADER_SIZE + payload.size).order(ByteOrder.BIG_ENDIAN)
        Header(type, flags, payload.size, timestampUs).writeTo(buffer)
        buffer.put(payload)
        return buffer.array()
    }

    fun hello(hello: Hello, timestampUs: Long): ByteArray =
        encode(MessageType.HELLO, hello.encodePayload(), timestampUs = timestampUs)

    fun requestKeyframe(timestampUs: Long): ByteArray =
        encode(MessageType.REQUEST_KEYFRAME, timestampUs = timestampUs)

    fun stats(stats: Stats, timestampUs: Long): ByteArray =
        encode(MessageType.STATS, stats.encodePayload(), timestampUs = timestampUs)

    fun pointer(event: PointerEvent, timestampUs: Long): ByteArray =
        encode(MessageType.POINTER, event.encodePayload(), timestampUs = timestampUs)

    fun scroll(event: ScrollEvent, timestampUs: Long): ByteArray =
        encode(MessageType.SCROLL, event.encodePayload(), timestampUs = timestampUs)

    fun input(message: InputMessage, timestampUs: Long): ByteArray = when (message) {
        is PointerEvent -> pointer(message, timestampUs)
        is ScrollEvent -> scroll(message, timestampUs)
    }

    /** Parses a message received by the client. Client-to-server types are a protocol error here. */
    fun parseServerMessage(header: Header, payload: ByteArray): ServerMessage = when (header.type) {
        MessageType.HELLO_ACK -> parseHelloAck(header, payload)
        MessageType.CODEC_CONFIG -> {
            if (payload.isEmpty()) throw ProtocolException("Empty CODEC_CONFIG")
            ServerMessage.CodecConfig(payload, header.timestampUs)
        }
        MessageType.FRAME -> {
            if (payload.isEmpty()) throw ProtocolException("Empty FRAME")
            ServerMessage.Frame(payload, header.flags and NexLink.FLAG_KEYFRAME != 0, header.timestampUs)
        }
        else -> throw ProtocolException("Unexpected message type 0x%02x from server".format(header.type))
    }

    private fun parseHelloAck(header: Header, payload: ByteArray): ServerMessage.HelloAck {
        if (payload.size != ServerMessage.HelloAck.PAYLOAD_SIZE) {
            throw ProtocolException("HELLO_ACK payload must be ${ServerMessage.HelloAck.PAYLOAD_SIZE} bytes")
        }
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
        checkMagic(buffer, "HELLO_ACK")
        val width = buffer.u16()
        val height = buffer.u16()
        val fps = buffer.u16()
        val codec = buffer.get().toInt() and 0xFF
        if (codec != NexLink.CODEC_H264) throw ProtocolException("Unsupported codec $codec")
        if (width == 0 || height == 0) throw ProtocolException("Invalid video size ${width}x$height")
        return ServerMessage.HelloAck(width, height, fps, codec, header.timestampUs)
    }
}

private fun requireU16(value: Int, name: String) = require(value in 0..0xFFFF) { "$name out of u16 range: $value" }

private fun ByteBuffer.u16(): Int = getShort().toInt() and 0xFFFF

private fun ByteBuffer.u32(): Long = getInt().toLong() and 0xFFFF_FFFFL

private fun checkMagic(buffer: ByteBuffer, what: String) {
    val magic = ByteArray(4).also { buffer.get(it) }
    if (!magic.contentEquals(NexLink.MAGIC)) throw ProtocolException("Bad $what magic")
}
