package com.bloomingdales.winkpos.link

import android.util.Log
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.util.concurrent.TimeUnit
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject

/**
 * PAX-agreed transport (BloomingdaleDemo sequence diagram): this app talks to
 * the PxRetailer REST service (PXRRS) running on this terminal, using form
 * variables as mailboxes.
 *
 *   startup -> POST /setVariable FOREGROUND=false; POST /subscribe
 *   incoming sale  <- notify IS_TRANS_STARTED=1
 *                  -> GET /getVariable START_TRANS_REQ_DATA (register's JSON)
 *   result  -> POST /sendBatchCmd [SetVariable TRANS_RESULT=json,
 *              DisplayForm EndTransaction]
 *
 * PXRRS delivers notify events by POSTing to a replyURL, so this transport
 * runs a minimal local HTTP listener for them.
 */
class PxrrsTransport(
    private val baseUrl: String,
    private val notifyPort: Int = DEFAULT_NOTIFY_PORT,
) : PosLinkTransport {

    private val http = OkHttpClient.Builder()
        .callTimeout(10, TimeUnit.SECONDS)
        .build()

    @Volatile private var listener: PosLinkTransport.Listener? = null
    @Volatile private var running = false
    @Volatile private var connected = false
    @Volatile private var subscribed = false
    private var notifySocket: ServerSocket? = null

    override fun start(listener: PosLinkTransport.Listener) {
        this.listener = listener
        running = true
        Thread(::notifyServerLoop, "PxrrsNotifyServer").apply { isDaemon = true }.start()
        Thread(::maintainLinkLoop, "PxrrsLink").apply { isDaemon = true }.start()
    }

    override fun stop() {
        running = false
        listener = null
        try { notifySocket?.close() } catch (_: Exception) { }
    }

    override fun send(message: PosMessage): Boolean {
        if (message.type != PosMessage.TYPE_PAYMENT_RESULT) return message.type == PosMessage.TYPE_HELLO
        val batch = JSONArray()
            .put(
                JSONObject()
                    .put("commandName", "SetVariable")
                    .put(
                        "variables",
                        JSONArray().put(
                            JSONObject().put("name", VAR_RESULT).put("value", message.toJson()),
                        ),
                    ),
            )
            .put(JSONObject().put("commandName", "DisplayForm").put("formName", FORM_END))
        return post("/sendBatchCmd", batch.toString())
    }

    // ----- Link maintenance (Phase 1 of the sequence diagram) -----

    private fun maintainLinkLoop() {
        while (running) {
            val alive = probe()
            if (alive && !subscribed) {
                subscribed = post(
                    "/setVariable",
                    JSONObject().put(
                        "variables",
                        JSONArray().put(JSONObject().put("name", VAR_FOREGROUND).put("value", "false")),
                    ).toString(),
                ) && post("/subscribe?replyURL=http://127.0.0.1:$notifyPort/notify", null)
            }

            val nowConnected = alive && subscribed
            if (nowConnected != connected) {
                connected = nowConnected
                if (nowConnected) {
                    listener?.onConnected()
                } else {
                    subscribed = false
                    listener?.onDisconnected()
                }
            }
            try { Thread.sleep(POLL_MS) } catch (_: InterruptedException) { return }
        }
    }

    // ----- Notify listener -----

    /**
     * Minimal HTTP/1.1 server, just enough for PXRRS's `POST /notify` with a
     * small JSON body. Not exposed off-device: bound to loopback.
     */
    private fun notifyServerLoop() {
        try {
            val server = ServerSocket()
            server.reuseAddress = true
            server.bind(InetSocketAddress("127.0.0.1", notifyPort))
            notifySocket = server
            while (running) {
                val client = server.accept()
                Thread {
                    try {
                        client.soTimeout = 5_000
                        val reader = BufferedReader(InputStreamReader(client.getInputStream()))
                        var contentLength = 0
                        var line = reader.readLine()
                        while (!line.isNullOrEmpty()) {
                            if (line.startsWith("Content-Length:", ignoreCase = true)) {
                                contentLength = line.substringAfter(':').trim().toIntOrNull() ?: 0
                            }
                            line = reader.readLine()
                        }
                        val body = CharArray(contentLength).let { buf ->
                            var read = 0
                            while (read < contentLength) {
                                val n = reader.read(buf, read, contentLength - read)
                                if (n < 0) break
                                read += n
                            }
                            String(buf, 0, read)
                        }
                        client.getOutputStream().write(
                            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                                .toByteArray(),
                        )
                        client.getOutputStream().flush()
                        handleNotify(body)
                    } catch (e: Exception) {
                        Log.w(TAG, "notify handler error: ${e.message}")
                    } finally {
                        try { client.close() } catch (_: Exception) { }
                    }
                }.start()
            }
        } catch (e: Exception) {
            if (running) Log.w(TAG, "notify server died: ${e.message}")
        }
    }

    private fun handleNotify(body: String) {
        Log.d(TAG, "notify: $body")
        val event = try { JSONObject(body) } catch (_: Exception) { return }
        // IS_TRANS_STARTED=1 -> the register published START_TRANS_REQ_DATA.
        if (event.optString("name") == EVENT_TRANS_STATE && event.optString("value") == "1") {
            Thread {
                getVariable(VAR_REQUEST)
                    ?.let { PosMessage.fromJson(it) }
                    ?.let { msg -> listener?.onMessage(msg) }
            }.start()
        }
    }

    // ----- REST helpers -----

    private fun probe(): Boolean = try {
        http.newCall(Request.Builder().url("$baseUrl/getPackageList").build())
            .execute().use { it.isSuccessful }
    } catch (_: Exception) {
        false
    }

    private fun post(path: String, jsonBody: String?): Boolean = try {
        val body = (jsonBody ?: "").toRequestBody(JSON)
        http.newCall(Request.Builder().url("$baseUrl$path").post(body).build())
            .execute().use { response ->
                val text = response.body?.string().orEmpty()
                val ok = response.isSuccessful &&
                    (runCatching { JSONObject(text).optString("resultCode") == "0" }.getOrNull() == true)
                if (!ok) Log.w(TAG, "POST $path -> $text")
                ok
            }
    } catch (e: Exception) {
        Log.w(TAG, "POST $path failed: ${e.message}")
        false
    }

    private fun getVariable(name: String): String? = try {
        http.newCall(Request.Builder().url("$baseUrl/getVariable?variableNames=$name").build())
            .execute().use { response ->
                val root = JSONObject(response.body?.string().orEmpty())
                val items = root.optJSONArray("resultItems")
                (0 until (items?.length() ?: 0))
                    .mapNotNull { items?.optJSONObject(it) }
                    .firstOrNull { it.optString("name") == name }
                    ?.optString("value")
            }
    } catch (e: Exception) {
        Log.w(TAG, "getVariable $name failed: ${e.message}")
        null
    }

    private companion object {
        const val TAG = "PxrrsTransport"
        const val DEFAULT_NOTIFY_PORT = 8484
        const val POLL_MS = 5_000L
        val JSON = "application/json; charset=utf-8".toMediaType()

        // Names from PAX's sequence diagram. TODO(PAX): confirm exact casing
        // in the form package they ship.
        const val VAR_REQUEST = "START_TRANS_REQ_DATA"
        const val VAR_RESULT = "TRANS_RESULT"
        const val VAR_FOREGROUND = "FOREGROUND"
        const val EVENT_TRANS_STATE = "IS_TRANS_STARTED"
        const val FORM_END = "EndTransaction"
    }
}
