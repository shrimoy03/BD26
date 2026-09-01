package com.bloomingdales.winkpos.link

import android.content.Context
import android.util.Log
import java.io.BufferedReader
import java.io.InputStreamReader
import java.math.BigDecimal
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.security.KeyStore
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
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
 *   startup -> POST /setVariable BOOL.FOREGROUND=false; POST /subscribe
 *   incoming sale  <- notify IS_TRANS_STARTED=1
 *                  -> GET /getVariable START_TRANS_REQ_DATA (register's JSON)
 *   result  -> POST /sendBatchCmd [SetVariable TRANS_RESULT=json,
 *              DisplayForm EndTransaction]
 *
 * That is the flow once PAX ships the custom Bloomingdale's package. A stock
 * PxRetail install defines none of IS_TRANS_STARTED / START_TRANS_REQ_DATA /
 * TRANS_RESULT (verified against an A3700 running PxRetailer 2.01.16), so
 * handleNotify also takes the stock route: the form's own tender buttons fire
 * PAYMENTSTATUS=face|palm and the amount is read from STR.AMOUNTOK, which the
 * register mirrors there as part of the basket sync.
 *
 * PXRRS delivers notify events by POSTing to a replyURL, so this transport
 * runs a minimal local HTTP listener for them. It serves HTTPS and asks for a
 * client certificate even on loopback — see buildClient.
 */
class PxrrsTransport(
    private val baseUrl: String,
    private val notifyPort: Int = DEFAULT_NOTIFY_PORT,
    private val context: Context? = null,
) : PosLinkTransport {

    private val http = buildClient(baseUrl, context)

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
        val name = event.optString("name")
        val value = event.optString("value")

        // IS_TRANS_STARTED=1 -> the register published START_TRANS_REQ_DATA.
        // Only present once PAX ships the custom Bloomingdale's package.
        if (name == EVENT_TRANS_STATE && value == "1") {
            Thread {
                getVariable(VAR_REQUEST)
                    ?.let { PosMessage.fromJson(it) }
                    ?.let { msg -> listener?.onMessage(msg) }
            }.start()
            return
        }

        // Stock-package path: the customer pressed a tender button on the
        // PxRetailer form (btnFace fires PAYMENTSTATUS=face). The custom
        // request mailbox does not exist on a stock install, but the register
        // already mirrors the basket total into STR.AMOUNTOK, so read the
        // amount from there and go straight into capture.
        if (name == EVENT_PAYMENT_STATUS) {
            val biometric = when (value.trim().lowercase()) {
                "face" -> "FACE"
                "palm" -> "PALM"
                else -> return
            }
            Thread {
                val cents = readAmountCents()
                if (cents == null) {
                    Log.w(TAG, "$EVENT_PAYMENT_STATUS=$value but $VAR_AMOUNT is empty — ignoring")
                    return@Thread
                }
                listener?.onMessage(
                    PosMessage(
                        type = PosMessage.TYPE_START_PAYMENT,
                        orderId = "PXRRS-${System.currentTimeMillis()}",
                        amountCents = cents,
                        currency = "USD",
                        method = biometric,
                    ),
                )
            }.start()
        }
    }

    /** STR.AMOUNTOK is a display string such as "$487.76"; convert to cents. */
    private fun readAmountCents(): Long? {
        val raw = getVariable(VAR_AMOUNT)?.replace(Regex("[^0-9.]"), "").orEmpty()
        if (raw.isBlank()) return null
        return try {
            BigDecimal(raw).movePointRight(2).toLong().takeIf { it > 0 }
        } catch (_: NumberFormatException) {
            null
        }
    }

    // ----- REST helpers -----

    /** PXRRS methods are POST-only; a GET just returns an empty reply. */
    private fun probe(): Boolean = try {
        http.newCall(
            Request.Builder().url("$baseUrl/getPackageList").post("".toRequestBody(JSON)).build(),
        ).execute().use { it.isSuccessful }
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

        /** PKCS#12 in assets/, derived from the PAX bundle's integrationCustomer.jks. */
        const val CLIENT_CERT_ASSET = "pxrrs-integration-client.p12"
        const val CLIENT_CERT_PASSWORD = "pax12345"

        /**
         * PXRRS serves HTTPS with PAX's self-signed chain and asks for a client
         * certificate; plain requests are dropped mid-handshake. Over http://
         * (or with no cert bundled) this stays a stock client.
         */
        fun buildClient(baseUrl: String, context: Context?): OkHttpClient {
            val builder = OkHttpClient.Builder().callTimeout(10, TimeUnit.SECONDS)
            if (!baseUrl.startsWith("https", ignoreCase = true)) return builder.build()

            val keyManagers = try {
                context?.assets?.open(CLIENT_CERT_ASSET)?.use { stream ->
                    val store = KeyStore.getInstance("PKCS12").apply {
                        load(stream, CLIENT_CERT_PASSWORD.toCharArray())
                    }
                    KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
                        .apply { init(store, CLIENT_CERT_PASSWORD.toCharArray()) }
                        .keyManagers
                }
            } catch (e: Exception) {
                Log.w(TAG, "no client certificate ($CLIENT_CERT_ASSET): ${e.message}")
                null
            }

            // PAX's chain is self-signed and the cert is issued for localhost;
            // trust it for the demo rather than shipping the root in the store.
            val trustAll = object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = Unit
                override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) = Unit
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            }

            return try {
                val ssl = SSLContext.getInstance("TLS")
                    .apply { init(keyManagers, arrayOf(trustAll), SecureRandom()) }
                builder
                    .sslSocketFactory(ssl.socketFactory, trustAll)
                    .hostnameVerifier { _, _ -> true }
                    .build()
            } catch (e: Exception) {
                Log.w(TAG, "TLS setup failed, falling back to default client: ${e.message}")
                builder.build()
            }
        }

        // PxDesigner variables are type-prefixed (BOOL./STR./INT./LIST.), so
        // the diagram's bare "FOREGROUND" is really BOOL.FOREGROUND — verified
        // against a live A3700 (PxRetailer 2.01.16); the unprefixed name is
        // rejected as unknown.
        //
        // The other two belong to PAX's custom Bloomingdale's package and are
        // absent on a stock PxRetail install. TODO(PAX): confirm their prefixes.
        const val VAR_REQUEST = "START_TRANS_REQ_DATA"
        const val VAR_RESULT = "TRANS_RESULT"
        const val VAR_FOREGROUND = "BOOL.FOREGROUND"
        const val EVENT_TRANS_STATE = "IS_TRANS_STARTED"
        const val FORM_END = "EndTransaction"

        // Stock PxRetail: the tender buttons on the payment form fire
        // PAYMENTSTATUS, and the register mirrors the basket total into
        // STR.AMOUNTOK. Both exist without PAX's custom package.
        const val EVENT_PAYMENT_STATUS = "PAYMENTSTATUS"
        const val VAR_AMOUNT = "STR.AMOUNTOK"
    }
}
