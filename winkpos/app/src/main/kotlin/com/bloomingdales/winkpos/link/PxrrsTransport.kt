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
    // Mailbox variables shared with the register (windowsterminal
    // PosSettings). Defaults are stock PxRetail names, verified to round-trip a
    // full order JSON; repoint them at PAX's custom names once that package
    // ships.
    private val requestVar: String = "STR.GENERIC_1",
    private val stateVar: String = "STR.GENERIC_2",
    private val resultVar: String = "STR.TRANSACTION_RESULT",
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
        Thread(::watchMailboxLoop, "PxrrsMailbox").apply { isDaemon = true }.start()
    }

    /**
     * The diagram's `notify IS_TRANS_STARTED=1`, done by polling.
     *
     * PXRRS on this terminal accepts a subscription and then never posts to the
     * replyURL — reproducible with emvDetectICCard, so it is not specific to
     * custom form events. Every other arrow in the diagram is already
     * get/setVariable and IS_TRANS_STARTED is really just a state flag, so the
     * register raises it here and this loop watches for it. Same protocol, no
     * notify.
     */
    private fun watchMailboxLoop() {
        while (running) {
            try { Thread.sleep(TRIGGER_POLL_MS) } catch (_: InterruptedException) { return }
            if (!connected) continue

            if (getVariable(stateVar)?.trim() != STATE_ORDER_READY) continue

            val order = getVariable(requestVar)
            val message = order?.let { PosMessage.fromJson(it) }
            if (message == null) {
                Log.w(TAG, "$stateVar=$STATE_ORDER_READY but $requestVar did not parse: $order")
                continue
            }

            // Claim the sale so a slow capture is not restarted next tick.
            setVariable(stateVar, STATE_IN_PROGRESS)
            Log.d(TAG, "order received: $order")
            listener?.onMessage(message)
        }
    }

    override fun stop() {
        running = false
        listener = null
        try { notifySocket?.close() } catch (_: Exception) { }
    }

    /**
     * Phase 4 of the sequence diagram: publish the biometric result and close
     * the sale out on the terminal.
     *
     * The diagram writes TRANS_RESULT and displays EndTransaction, both of
     * which belong to PAX's custom package. On a stock PxRetail install neither
     * exists, so the result also goes to STR.TRANSACTION_RESULT (which the
     * register polls) and the screen falls back to the stock receipt/idle
     * forms. The two writes are separate calls on purpose — batched, the
     * missing variable would fail the whole command.
     */
    override fun send(message: PosMessage): Boolean {
        if (message.type != PosMessage.TYPE_PAYMENT_RESULT) return message.type == PosMessage.TYPE_HELLO

        // Phase 4: publish the outcome, then raise the flag the register polls.
        // Order matters — the flag must not go up before the result is readable.
        val published = setVariable(resultVar, message.toJson())
        val flagged = setVariable(stateVar, STATE_RESULT_READY)

        val form = if (message.status == PosMessage.STATUS_APPROVED) FORM_THANKS else FORM_IDLE
        val shown = post("/displayForm?formName=$form", null)

        // Capture is done; hand the screen back to PxRetailer.
        setVariable(VAR_FOREGROUND, "true")

        return published && flagged && shown
    }

    private fun setVariable(name: String, value: String): Boolean = post(
        "/setVariable",
        JSONObject().put(
            "variables",
            JSONArray().put(JSONObject().put("name", name).put("value", value)),
        ).toString(),
    )

    // ----- Link maintenance (Phase 1 of the sequence diagram) -----

    private fun maintainLinkLoop() {
        while (running) {
            val alive = probe()

            if (alive && !subscribed) {
                // Both are best-effort. The sale runs through the variable
                // mailboxes, so neither the foreground flag (absent on some
                // packages) nor the notify subscription may gate the link —
                // treating them as required would leave the app permanently
                // "disconnected" and the mailbox loop would never run.
                setVariable(VAR_FOREGROUND, "false")
                subscribe()
                subscribed = true
            }

            if (alive != connected) {
                connected = alive
                if (alive) {
                    listener?.onConnected()
                } else {
                    subscribed = false // re-run the init handshake on reconnect
                    listener?.onDisconnected()
                }
            }
            try { Thread.sleep(POLL_MS) } catch (_: InterruptedException) { return }
        }
    }

    // ----- Subscribe (Phase 1) -----

    /**
     * Shaped after PAX's working curl recipe (verified against a live A3700
     * from the register side — Subscribe returns Success and the terminal
     * records the replyAddress):
     *
     *   curl 'https://<terminal>:9090/subscribe?replyURL=<host>%3A<port>' \
     *        --form 'fileName=@server_pci7.cert'
     *
     * The replyURL is bare host:port (no scheme, no path) and the callback
     * server's certificate rides along as a multipart part named "fileName" —
     * PXRRS needs it to authenticate the HTTPS callback before it will post
     * anything there. The PEM is extracted from the very keystore the notify
     * listener serves TLS with, so the two can never drift apart. With no
     * keystore bundled this degrades to the old bare subscribe.
     */
    private fun subscribe(): Boolean {
        val reply = java.net.URLEncoder.encode("127.0.0.1:$notifyPort", "UTF-8")
        val path = "/subscribe?replyURL=$reply"
        val pem = notifyCertPem() ?: return post(path, null)

        return try {
            val form = okhttp3.MultipartBody.Builder()
                .setType(okhttp3.MultipartBody.FORM)
                .addFormDataPart(
                    "fileName",
                    "server_pci7.cert",
                    pem.toRequestBody("application/octet-stream".toMediaType()),
                )
                .build()
            http.newCall(Request.Builder().url("$baseUrl$path").post(form).build())
                .execute().use { response ->
                    val text = response.body?.string().orEmpty()
                    val ok = response.isSuccessful &&
                        runCatching { JSONObject(text).optString("resultCode") == "0" }.getOrNull() == true
                    Log.d(TAG, "subscribe (with callback cert) -> $text")
                    ok
                }
        } catch (e: Exception) {
            Log.w(TAG, "subscribe with certificate failed: ${e.message}")
            post(path, null)
        }
    }

    /** The notify keystore, or null when the asset is not bundled. */
    private fun loadNotifyKeystore(): KeyStore? = try {
        context?.assets?.open(NOTIFY_CERT_ASSET)?.use { stream ->
            KeyStore.getInstance("PKCS12").apply {
                load(stream, NOTIFY_CERT_PASSWORD.toCharArray())
            }
        }
    } catch (e: Exception) {
        Log.w(TAG, "no notify keystore ($NOTIFY_CERT_ASSET): ${e.message}")
        null
    }

    /** PEM of the notify listener's certificate, for the subscribe attachment. */
    private fun notifyCertPem(): String? = try {
        loadNotifyKeystore()?.let { store ->
            val alias = store.aliases().toList().firstOrNull() ?: return null
            val der = store.getCertificate(alias)?.encoded ?: return null
            val b64 = android.util.Base64.encodeToString(der, android.util.Base64.NO_WRAP)
            buildString {
                append("-----BEGIN CERTIFICATE-----\n")
                b64.chunked(64).forEach { append(it).append('\n') }
                append("-----END CERTIFICATE-----\n")
            }
        }
    } catch (e: Exception) {
        Log.w(TAG, "could not export notify certificate: ${e.message}")
        null
    }

    // ----- Notify listener -----

    /**
     * Minimal HTTP/1.1 server, just enough for PXRRS's `POST /notify` with a
     * small JSON body. Not exposed off-device: bound to loopback. Serves TLS
     * with the bundled PAX identity when present (PXRRS is not known to
     * deliver to a plain-http callback), plain HTTP otherwise.
     */
    private fun notifyServerLoop() {
        try {
            val server = createNotifyServerSocket()
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

    /**
     * TLS server socket with the bundled PAX identity — the same one whose
     * certificate went up with /subscribe — or a plain socket without it.
     */
    private fun createNotifyServerSocket(): ServerSocket {
        val store = loadNotifyKeystore()
        val socket = if (store != null) {
            try {
                val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
                    .apply { init(store, NOTIFY_CERT_PASSWORD.toCharArray()) }
                val ssl = SSLContext.getInstance("TLS")
                    .apply { init(kmf.keyManagers, null, SecureRandom()) }
                ssl.serverSocketFactory.createServerSocket()
            } catch (e: Exception) {
                Log.w(TAG, "notify TLS setup failed, serving plain http: ${e.message}")
                ServerSocket()
            }
        } else {
            ServerSocket()
        }
        socket.reuseAddress = true
        socket.bind(InetSocketAddress("127.0.0.1", notifyPort))
        return socket
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
         * PKCS#12 in assets/ with the PAX Multilane server identity the notify
         * listener presents — same file as windowsterminal/certs/, copy it in:
         *   cp windowsterminal/certs/pxrrs-notify-server.p12 winkpos/app/src/main/assets/
         */
        const val NOTIFY_CERT_ASSET = "pxrrs-notify-server.p12"
        const val NOTIFY_CERT_PASSWORD = "pax12345"

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
        const val VAR_FOREGROUND = "BOOL.FOREGROUND"
        const val EVENT_TRANS_STATE = "IS_TRANS_STARTED"

        // Stock PxRetail: the tender buttons on the payment form fire
        // PAYMENTSTATUS, and the register mirrors the basket total into
        // STR.AMOUNTOK. Both exist without PAX's custom package.
        const val EVENT_PAYMENT_STATUS = "PAYMENTSTATUS"
        const val VAR_AMOUNT = "STR.AMOUNTOK"
        const val TRIGGER_POLL_MS = 1_000L

        // Handshake states in the state mailbox. PXRRS rejects setVariable with
        // an empty value, so idle is "0" rather than blank.
        const val STATE_ORDER_READY = "1"
        const val STATE_RESULT_READY = "2"
        const val STATE_IN_PROGRESS = "3"
        const val FORM_THANKS = "ThankYouScreen"
        const val FORM_IDLE = "BackgroundScreen"
    }
}
