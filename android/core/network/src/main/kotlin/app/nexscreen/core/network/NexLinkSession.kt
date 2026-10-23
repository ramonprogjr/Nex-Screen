package app.nexscreen.core.network

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException

sealed interface ConnectionState {
    data object Idle : ConnectionState

    data class Connecting(val attempt: Int) : ConnectionState

    /** TCP is up and HELLO was sent; waiting for HELLO_ACK. */
    data object Handshaking : ConnectionState

    data class Streaming(val videoWidth: Int, val videoHeight: Int, val fps: Int) : ConnectionState

    /** The last attempt failed; the next one starts in [retryInMs]. */
    data class Waiting(val attempt: Int, val retryInMs: Long, val reason: String) : ConnectionState
}

/** Exponential reconnect delay. */
class Backoff(
    private val initialMs: Long = 250,
    private val maxMs: Long = 2_000,
) {
    private var nextMs = initialMs

    fun next(): Long = nextMs.also { nextMs = (nextMs * 2).coerceAtMost(maxMs) }

    fun reset() {
        nextMs = initialMs
    }
}

/**
 * Client side of a NexLink v0-usb session: connects, sends HELLO, dispatches the companion's messages to
 * [listener], sends STATS every [statsIntervalMs] and REQUEST_KEYFRAME on demand, and reconnects with backoff
 * when the companion is not running or the connection drops.
 *
 * With `adb reverse`, the TCP connect succeeds even when nothing listens on the PC and the socket is closed
 * right away, so a session only counts as established after HELLO_ACK.
 */
class NexLinkSession(
    private val transportFactory: () -> DisplayTransport,
    private val helloProvider: () -> Hello,
    private val statsProvider: () -> Stats,
    private val listener: Listener,
    private val clock: MonotonicClock = MonotonicClock.SYSTEM,
    private val backoff: Backoff = Backoff(),
    private val handshakeTimeoutMs: Long = 5_000,
    private val statsIntervalMs: Long = 1_000,
    private val minKeyframeRequestIntervalMs: Long = 300,
) {
    /** Callbacks run on the session's receive coroutine, in wire order. */
    interface Listener {
        fun onHelloAck(ack: ServerMessage.HelloAck)
        fun onCodecConfig(config: ServerMessage.CodecConfig)
        fun onFrame(frame: ServerMessage.Frame)
        fun onDisconnected() {}
    }

    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Idle)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private val keyframeRequests = Channel<Unit>(Channel.CONFLATED)

    // Touch input waiting to be written. Bounded so a stalled connection cannot grow it without limit.
    private val inputQueue = Channel<InputMessage>(INPUT_QUEUE_CAPACITY)

    // True between HELLO_ACK and the end of the connection: input is only accepted while the video is live
    @Volatile
    private var inputOpen = false

    // Set once HELLO_ACK arrives on the current connection
    @Volatile
    private var established = false

    /** Asks the companion for a keyframe. Requests are coalesced and rate-limited. */
    fun requestKeyframe() {
        keyframeRequests.trySend(Unit)
    }

    /**
     * Queues a POINTER or SCROLL message for the companion. Thread-safe and non-blocking. Returns false (and drops
     * the message) when no session is established or the queue is full; input is never replayed on a later
     * connection.
     */
    fun sendInput(message: InputMessage): Boolean = inputOpen && inputQueue.trySend(message).isSuccess

    /** Runs until the calling coroutine is cancelled. */
    suspend fun run() {
        var attempt = 0
        backoff.reset()
        try {
            while (true) {
                attempt++
                _state.value = ConnectionState.Connecting(attempt)
                val transport = transportFactory()
                established = false
                val reason = try {
                    runConnection(transport)
                    "connection closed by peer"
                } catch (e: CancellationException) {
                    throw e
                } catch (e: IOException) {
                    e.message ?: e.javaClass.simpleName
                } catch (e: IllegalArgumentException) {
                    e.message ?: e.javaClass.simpleName
                } finally {
                    inputOpen = false
                    withContext(NonCancellable) {
                        transport.disconnect()
                        listener.onDisconnected()
                    }
                }
                if (established) {
                    attempt = 0
                    backoff.reset()
                }
                val wait = backoff.next()
                _state.value = ConnectionState.Waiting(attempt, wait, reason)
                delay(wait)
            }
        } finally {
            _state.value = ConnectionState.Idle
        }
    }

    private suspend fun runConnection(transport: DisplayTransport) = coroutineScope {
        // Blocking socket reads ignore cancellation, so close the transport as soon as this scope is cancelled
        launch {
            try {
                awaitCancellation()
            } finally {
                withContext(NonCancellable) { transport.disconnect() }
            }
        }

        transport.connect()
        _state.value = ConnectionState.Handshaking
        while (keyframeRequests.tryReceive().isSuccess) Unit // the stream starts with a keyframe anyway
        transport.sendControl(Messages.hello(helloProvider(), clock.nowUs()))

        val handshakeTimer = launch {
            delay(handshakeTimeoutMs)
            transport.disconnect()
        }
        val first = transport.receive()
        handshakeTimer.cancel()
        if (first == null) throw IOException("closed before HELLO_ACK")
        if (first !is ServerMessage.HelloAck) throw ProtocolException("expected HELLO_ACK first")
        established = true
        while (inputQueue.tryReceive().isSuccess) Unit // stale input from a previous connection
        inputOpen = true
        dispatch(first)

        launch {
            while (true) {
                delay(statsIntervalMs)
                transport.sendControl(Messages.stats(statsProvider(), clock.nowUs()))
            }
        }
        launch {
            for (message in inputQueue) {
                transport.sendControl(Messages.input(message, clock.nowUs()))
            }
        }
        launch {
            for (request in keyframeRequests) {
                transport.sendControl(Messages.requestKeyframe(clock.nowUs()))
                delay(minKeyframeRequestIntervalMs)
            }
        }

        while (true) {
            val message = transport.receive() ?: break
            dispatch(message)
        }
        coroutineContext.cancelChildren()
    }

    private companion object {
        const val INPUT_QUEUE_CAPACITY = 1024
    }

    private fun dispatch(message: ServerMessage) {
        when (message) {
            is ServerMessage.HelloAck -> {
                _state.value = ConnectionState.Streaming(message.videoWidth, message.videoHeight, message.fps)
                listener.onHelloAck(message)
            }
            is ServerMessage.CodecConfig -> listener.onCodecConfig(message)
            is ServerMessage.Frame -> listener.onFrame(message)
        }
    }
}
