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
class WebSocketTransport(private val url: String) : PosLinkTransport {

    private val client = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        .build()

    @Volatile private var listener: PosLinkTransport.Listener? = null
    @Volatile private var socket: WebSocket? = null
    @Volatile private var running = false

    override fun start(listener: PosLinkTransport.Listener) {
        this.listener = listener
        running = true
        connect()
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
        val request = Request.Builder().url(url).build()
        client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                socket = webSocket
                webSocket.send(PosMessage(PosMessage.TYPE_HELLO).toJson())
                listener?.onConnected()
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                PosMessage.fromJson(text)?.let { listener?.onMessage(it) }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                Log.w(TAG, "connection lost: ${t.message}")
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
            listener?.onDisconnected()
        }
        if (running) {
            Thread {
                Thread.sleep(RETRY_DELAY_MS)
                connect()
            }.start()
        }
    }

    private companion object {
        const val TAG = "WebSocketTransport"
        const val RETRY_DELAY_MS = 3_000L
    }
}
