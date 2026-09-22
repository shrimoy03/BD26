package com.bloomingdales.winkpos.link

import android.util.Log
import java.util.concurrent.TimeUnit
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

/**
 * LAN transport: connects to the register's WebSocket server
 * (windowsterminal Services/TerminalLink.cs, ws://<register-ip>:8181/pos)
 * and reconnects forever with a fixed backoff until stopped.
 */
class WebSocketTransport(
    /** Register addresses to try, best first; re-evaluated on every (re)connect. */
    private val candidates: () -> List<String>,
    /** Called with the address that actually opened, so it can be remembered. */
    private val onConnectedTo: (String) -> Unit = {},
    /** Turns a candidate into the URL actually dialled (adds this terminal's identity). */
    private val decorate: (String) -> String = { it },
    /** The register at this address refused us (HTTP 403: it drives another terminal). */
    private val onRejected: (String) -> Unit = {},
) : PosLinkTransport {

    constructor(url: String) : this({ listOf(url) })

    /** Which candidate the next attempt uses; rotates on failure. */
    private var attempt = 0

    /** Address of the open socket, null while disconnected. */
    @Volatile var connectedUrl: String? = null
        private set

    /**
     * The register driving the terminal changed: drop the current socket and
     * connect to whatever the candidates now say. No-op while disconnected —
     * the retry loop re-evaluates the candidates on its own.
     */
    fun reconnect(reason: String) {
        val current = socket ?: return
        Log.i(TAG, "reconnecting — $reason")
        attempt = 0
        current.close(1000, reason) // onClosed -> dropAndRetry -> connect()
    }

    private val client = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        .build()

    @Volatile private var listener: PosLinkTransport.Listener? = null
    @Volatile private var socket: WebSocket? = null
    @Volatile private var running = false

    override fun start(listener: PosLinkTransport.Listener) {
        this.listener = listener
        running = true
        // Off the main thread: start() is called from Application.onCreate and
        // connect() resolves the candidates, which asks the terminal's PXRRS
        // over HTTP — NetworkOnMainThreadException otherwise, so the first
        // attempt always fell through to the compiled-in address and only the
        // retries (already on a worker thread) discovered the real register.
        Thread({ connect() }, "PosLinkWsConnect").apply { isDaemon = true }.start()
    }

    override fun stop() {
        running = false
        listener = null
        socket?.close(1000, "app shutting down")
        socket = null
    }

    override fun send(message: PosMessage): Boolean =
        socket?.send(message.toJson()) ?: false

    private fun connect() {
        if (!running) return
        val urls = candidates()
        if (urls.isEmpty()) {
            Log.w(TAG, "no register address known — retrying discovery")
            scheduleRetry()
            return
        }
        val url = urls[attempt % urls.size]
        Log.d(TAG, "connecting to $url (${attempt % urls.size + 1}/${urls.size})")
        val request = Request.Builder().url(decorate(url)).build()
        client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                socket = webSocket
                connectedUrl = url
                attempt = 0
                onConnectedTo(url)
                webSocket.send(PosMessage(PosMessage.TYPE_HELLO).toJson())
                listener?.onConnected()
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                PosMessage.fromJson(text)?.let { listener?.onMessage(it) }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (response?.code == 403) {
                    Log.w(TAG, "$url refused us: ${response.header("X-Reject-Reason") ?: "403"} — that register drives another terminal")
                    onRejected(url)
                } else {
                    Log.w(TAG, "connection lost: ${t.message}")
                }
                dropAndRetry(webSocket)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                dropAndRetry(webSocket)
            }
        })
    }

    private fun dropAndRetry(closed: WebSocket) {
        if (socket === closed) {
            socket = null
            connectedUrl = null
            listener?.onDisconnected()
        } else {
            attempt++ // never opened: try the next candidate
        }
        scheduleRetry()
    }

    private fun scheduleRetry() {
        if (!running) return
        Thread {
            Thread.sleep(RETRY_DELAY_MS)
            connect()
        }.start()
    }

    private companion object {
        const val TAG = "WebSocketTransport"
        const val RETRY_DELAY_MS = 3_000L
    }
}
