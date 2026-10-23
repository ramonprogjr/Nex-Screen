package app.nexscreen.core.network

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Transport between the tablet and the companion (PROJETO.md section 8), so the session does not know
 * whether it runs over USB or, later, Wi-Fi.
 */
interface DisplayTransport {
    suspend fun connect()

    /** Sends one already-framed message. */
    suspend fun sendControl(message: ByteArray)

    /** Next message from the companion, or null when the peer closed the connection. */
    suspend fun receive(): ServerMessage?

    /** Closes the connection. Safe to call more than once and from any thread; unblocks a pending [receive]. */
    suspend fun disconnect()
}

/** NexLink v0-usb: plain TCP to the port forwarded by `adb reverse`. */
class TcpTransport(
    private val host: String = NexLink.DEFAULT_HOST,
    private val port: Int = NexLink.DEFAULT_PORT,
    private val connectTimeoutMs: Int = 2_000,
) : DisplayTransport {
    private val socket = Socket()
    private val sendLock = Mutex()
    private var reader: MessageReader? = null
    private var output: OutputStream? = null

    override suspend fun connect() = withContext(Dispatchers.IO) {
        socket.tcpNoDelay = true
        socket.connect(InetSocketAddress(host, port), connectTimeoutMs)
        socket.tcpNoDelay = true
        reader = MessageReader(BufferedInputStream(socket.getInputStream(), 256 * 1024))
        output = socket.getOutputStream()
    }

    override suspend fun sendControl(message: ByteArray) {
        val out = output ?: throw IOException("Not connected")
        sendLock.withLock {
            withContext(Dispatchers.IO) {
                out.write(message)
                out.flush()
            }
        }
    }

    override suspend fun receive(): ServerMessage? = withContext(Dispatchers.IO) {
        val r = reader ?: throw IOException("Not connected")
        val raw = r.read() ?: return@withContext null
        Messages.parseServerMessage(raw.header, raw.payload)
    }

    override suspend fun disconnect() {
        close()
    }

    fun close() {
        try {
            socket.close()
        } catch (_: IOException) {
        }
    }
}
