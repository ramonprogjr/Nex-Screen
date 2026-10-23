package app.nexscreen.core.network

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress
import java.net.ServerSocket
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.concurrent.thread

/** End-to-end over loopback TCP with a fake companion. */
class SessionTest {

    @Test
    fun handshakeStreamAndKeyframeRequest() = runBlocking {
        val server = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val received = CopyOnWriteArrayList<ServerMessage>()
        val gotFrame = CompletableDeferred<Unit>()
        val serverSaw = CopyOnWriteArrayList<RawMessage>()
        val sawKeyframeRequest = CompletableDeferred<Unit>()

        val companion = thread {
            server.accept().use { socket ->
                socket.tcpNoDelay = true
                val reader = MessageReader(socket.getInputStream())
                val out = socket.getOutputStream()
                val hello = reader.read()!!
                serverSaw += hello
                out.write(Messages.encode(MessageType.HELLO_ACK, ServerMessage.HelloAck(1920, 1200, 30, 1, 0).encodePayload(), timestampUs = 1))
                out.write(Messages.encode(MessageType.CODEC_CONFIG, byteArrayOf(0, 0, 0, 1, 0x67), timestampUs = 2))
                out.write(Messages.encode(MessageType.FRAME, byteArrayOf(0, 0, 0, 1, 0x65), flags = 1, timestampUs = 3))
                out.flush()
                while (true) {
                    val message = reader.read() ?: break
                    serverSaw += message
                    if (message.header.type == MessageType.REQUEST_KEYFRAME) {
                        sawKeyframeRequest.complete(Unit)
                        break
                    }
                }
            }
        }

        val session = NexLinkSession(
            transportFactory = { TcpTransport(port = server.localPort) },
            helloProvider = { Hello(1920, 1200, 240, 0) },
            statsProvider = { Stats.EMPTY },
            listener = object : NexLinkSession.Listener {
                override fun onHelloAck(ack: ServerMessage.HelloAck) {
                    received += ack
                }

                override fun onCodecConfig(config: ServerMessage.CodecConfig) {
                    received += config
                }

                override fun onFrame(frame: ServerMessage.Frame) {
                    received += frame
                    gotFrame.complete(Unit)
                }
            },
            statsIntervalMs = 50,
        )
        val job = launch(Dispatchers.IO) { session.run() }

        withTimeout(5_000) {
            gotFrame.await()
            assertEquals(ConnectionState.Streaming(1920, 1200, 30), session.state.first { it is ConnectionState.Streaming })
            session.requestKeyframe()
            sawKeyframeRequest.await()
        }
        job.cancelAndJoin()
        companion.join(5_000)
        server.close()

        assertEquals(MessageType.HELLO, serverSaw.first().header.type)
        assertEquals(Hello(1920, 1200, 240, 0), Hello.decodePayload(serverSaw.first().payload))
        assertTrue(received[0] is ServerMessage.HelloAck)
        assertTrue(received[1] is ServerMessage.CodecConfig)
        assertTrue((received[2] as ServerMessage.Frame).keyframe)
        assertEquals(ConnectionState.Idle, session.state.value)
    }

    @Test
    fun retriesWhenNothingListens() = runBlocking {
        // Grab a free port and close it so connects are refused
        val port = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }
        val session = NexLinkSession(
            transportFactory = { TcpTransport(port = port, connectTimeoutMs = 500) },
            helloProvider = { Hello(1920, 1200, 240, 0) },
            statsProvider = { Stats.EMPTY },
            listener = object : NexLinkSession.Listener {
                override fun onHelloAck(ack: ServerMessage.HelloAck) = Unit
                override fun onCodecConfig(config: ServerMessage.CodecConfig) = Unit
                override fun onFrame(frame: ServerMessage.Frame) = Unit
            },
            backoff = Backoff(initialMs = 10, maxMs = 20),
        )
        val job = launch(Dispatchers.IO) { session.run() }
        withTimeout(5_000) {
            session.state.first { it is ConnectionState.Waiting && it.attempt >= 3 }
        }
        job.cancelAndJoin()
    }
}
