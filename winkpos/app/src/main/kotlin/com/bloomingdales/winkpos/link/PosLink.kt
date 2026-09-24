package com.bloomingdales.winkpos.link

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
import androidx.core.app.NotificationCompat
import com.bloomingdales.winkpos.BuildConfig
import java.util.concurrent.CopyOnWriteArrayList

/**
 * App-wide facade over the register link. Initialized once from the
 * Application class; activities register as listeners while visible.
 *
 * Transport is chosen by POS_LINK_MODE in local.properties:
 *   "pxrrs" — PAX-agreed flow: REST to the PxRetailer service on this
 *             terminal (PXRRS), form variables as mailboxes. Optional
 *             POS_LINK_PXRRS_URL overrides https://127.0.0.1:9090. Needs
 *             assets/pxrrs-integration-client.p12 for the mutual-TLS
 *             handshake — see windowsterminal/certs/README.md.
 *   "ws"    — WebSocket to the register app itself (needs POS_LINK_WS_URL,
 *             e.g. ws://192.168.1.50:8181/pos). Works today over Wi-Fi.
 *   "pcl"   — PCL frames over the USB serial port via JPxSerialServer on the
 *             register PC (needs PAX's NeptuneLite library, see SerialIo.kt).
 *   ""      — link disabled; the app runs the standalone demo flow.
 *
 * Register-driven sale state lives here (not in an Activity) because the
 * command can arrive on any screen.
 */
object PosLink {

    interface Listener {
        fun onLinkStateChanged(connected: Boolean) {}
        fun onStartPayment(orderId: String, amountCents: Long) {}
        fun onCancelPayment(orderId: String?) {}
        /** Check-in mode: the cashier rang more items; the sale's amount moved. */
        fun onAmountChanged(orderId: String, amountCents: Long) {}
        /** Check-in mode: the cashier pressed Complete Payment — charge now. */
        fun onCompletePayment(orderId: String, amountCents: Long) {}
    }

    /** The sale the register asked us to collect; null orderId = no pending sale. */
    object RegisterSale {
        @Volatile var orderId: String? = null
            private set
        @Volatile var amountCents: Long = 0
            private set
        /** Check-in mode: identified first, charged when the register says Complete Payment. */
        @Volatile var checkin: Boolean = false
            private set

        val isPending: Boolean get() = orderId != null

        internal fun set(orderId: String, amountCents: Long, checkin: Boolean = false) {
            this.orderId = orderId
            this.amountCents = amountCents
            this.checkin = checkin
        }

        internal fun updateAmount(amountCents: Long) {
            this.amountCents = amountCents
        }

        internal fun clear() {
            orderId = null
            amountCents = 0
            checkin = false
        }
    }

    @Volatile var isConnected: Boolean = false
        private set
    val isEnabled: Boolean get() = transport != null

    private var transport: PosLinkTransport? = null
    private val listeners = CopyOnWriteArrayList<Listener>()
    private val mainHandler = Handler(Looper.getMainLooper())
    private var appContext: Context? = null

    // Capture-launch dedupe (see TYPE_START_PAYMENT below).
    private var lastLaunchOrderId: String? = null
    private var lastLaunchAtMs: Long = 0

    /** Order ids whose launch intent WelcomeActivity has already handled. */
    private val handledLaunches = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()

    /** WelcomeActivity reports that it received the launch for [orderId]; the fallback is then not needed. */
    fun noteLaunchHandled(orderId: String?) {
        if (orderId != null) handledLaunches.add(orderId)
    }
    private const val LAUNCH_DEDUPE_MS = 8_000L // below the register 10s retry window
    private const val DIRECT_LAUNCH_GRACE_MS = 1_200L

    fun init(context: Context) {
        appContext = context.applicationContext
        if (transport != null) return
        transport = when (BuildConfig.POS_LINK_MODE) {
            "ws" -> {
                // The register is found at runtime (Settings override, the
                // address the register parks in PxRetailer, the last one that
                // worked, then the compiled-in default) — see RegisterAddress.
                val app = context.applicationContext
                WebSocketTransport(
                    candidates = { RegisterAddress.candidates(app) },
                    onConnectedTo = { url -> RegisterAddress.rememberGood(app, url) },
                    decorate = { url -> RegisterAddress.withIdentity(url) },
                    onRejected = { url -> RegisterAddress.markRejected(url) },
                    onLostConnection = { RegisterAddress.invalidate() },
                ).also { ws -> startRegisterWatch(app, ws) }
            }
            // PXRRS requires HTTPS with a client certificate even on loopback,
            // so the default is https:// and the transport needs a Context to
            // read the bundled keystore out of assets.
            "pxrrs" -> PxrrsTransport(
                BuildConfig.POS_LINK_PXRRS_URL.ifBlank { PxrrsTransport.DEFAULT_URL },
                context = context.applicationContext,
                requestVar = BuildConfig.POS_REQUEST_VAR.ifBlank { "STR.GENERIC_1" },
                stateVar = BuildConfig.POS_STATE_VAR.ifBlank { "STR.GENERIC_2" },
                resultVar = BuildConfig.POS_RESULT_VAR.ifBlank { "STR.TRANSACTION_RESULT" },
            )
            "pcl" -> PclSerialTransport(PaxNeptuneSerialIo(context.applicationContext))
            else -> null
        }
        transport?.start(object : PosLinkTransport.Listener {
            override fun onConnected() = onMain {
                isConnected = true
                listeners.forEach { it.onLinkStateChanged(true) }
            }

            override fun onDisconnected() = onMain {
                isConnected = false
                listeners.forEach { it.onLinkStateChanged(false) }
            }

            override fun onMessage(message: PosMessage) = onMain { dispatch(message) }
        })
    }

    fun addListener(listener: Listener) = listeners.add(listener).let { }
    fun removeListener(listener: Listener) = listeners.remove(listener).let { }

    /**
     * Give the display back to PxRetailer. Used when the customer backs out of
     * a register-driven scan from the failure popup: the sale is reported as
     * CANCELLED over the link, but that alone leaves this app's idle page on
     * top of the terminal until the register's next screen command. The
     * PXRRS transport already flips the flag as part of sending a result, so
     * this only has work to do on the other transports.
     */
    fun returnToRetailer() {
        if (transport is PxrrsTransport) return
        val ctx = appContext ?: return
        PxrrsTransport.handScreenBackToRetailer(ctx, BuildConfig.POS_LINK_PXRRS_URL)
    }

    /** Check-in mode: tell the register the customer is identified and WinkPay is waiting. */
    fun sendCheckinReady(method: String?, customerLabel: String) {
        val orderId = RegisterSale.orderId ?: return
        transport?.send(
            PosMessage(
                type = PosMessage.TYPE_CHECKIN_READY,
                orderId = orderId,
                method = method,
                customerLabel = customerLabel,
            ),
        )
    }

    /** Report the outcome of the pending register sale and clear it. */
    fun sendResult(
        status: String,
        method: String? = null,
        reason: String? = null,
        token: String? = null,
        /** What was actually charged when a coupon reduced the register's amount. */
        chargedCents: Long? = null,
        discountCents: Long? = null,
        discountLabel: String? = null,
    ) {
        val orderId = RegisterSale.orderId ?: return
        val amount = chargedCents ?: RegisterSale.amountCents
        RegisterSale.clear()
        transport?.send(
            PosMessage(
                type = PosMessage.TYPE_PAYMENT_RESULT,
                orderId = orderId,
                amountCents = if (status == PosMessage.STATUS_APPROVED) amount else null,
                status = status,
                method = method,
                reason = reason,
                token = token,
                discountCents = discountCents?.takeIf { it > 0 },
                discountLabel = discountLabel?.takeIf { discountCents != null && discountCents > 0 },
            ),
        )
    }

    private fun dispatch(message: PosMessage) {
        when (message.type) {
            PosMessage.TYPE_START_PAYMENT -> {
                val orderId = message.orderId ?: return
                val amount = message.amountCents ?: run {
                    Log.w(TAG, "START_PAYMENT with no amountCents: ${message.toJson()}")
                    return
                }
                val checkin = message.checkin == true
                Log.d(TAG, "START_PAYMENT order=$orderId amount=$amount method=${message.method} checkin=$checkin")
                RegisterSale.set(orderId, amount, checkin)
                listeners.forEach { it.onStartPayment(orderId, amount) }

                // FACE/PALM = the customer already picked a biometric tender
                // on the PxRetailer form — bring this app to the foreground and
                // go straight into WinkPay capture, even from the background.
                //
                // Idempotent: a spammed Face button (or the register double-
                // sending) must not relaunch the capture activity — a second
                // launch intent to the singleTask WelcomeActivity would clear
                // the in-progress capture screen above it.
                val biometric = message.method?.lowercase()
                if (biometric == "face" || biometric == "palm") {
                    val now = android.os.SystemClock.elapsedRealtime()
                    if (orderId == lastLaunchOrderId && now - lastLaunchAtMs < LAUNCH_DEDUPE_MS) {
                        Log.d(TAG, "duplicate START_PAYMENT for $orderId ignored " +
                            "(${now - lastLaunchAtMs}ms since launch)")
                    } else {
                        lastLaunchOrderId = orderId
                        lastLaunchAtMs = now
                        appContext?.let { ctx -> launchCapture(ctx, biometric) }
                    }
                }
            }
            PosMessage.TYPE_DISPLAY_CART -> {
                // Live amount for a check-in in progress. Any other cart mirror
                // is PxRetailer's business and ignored here.
                if (message.checkin != true) return
                val orderId = RegisterSale.orderId ?: return
                if (!RegisterSale.checkin) return
                val amount = message.amountCents ?: return
                if (amount != RegisterSale.amountCents) {
                    RegisterSale.updateAmount(amount)
                    Log.d(TAG, "check-in amount -> $amount")
                    listeners.forEach { it.onAmountChanged(orderId, amount) }
                }
            }
            PosMessage.TYPE_COMPLETE_PAYMENT -> {
                val orderId = RegisterSale.orderId
                if (orderId == null || (message.orderId != null && message.orderId != orderId)) {
                    Log.w(TAG, "COMPLETE_PAYMENT for ${message.orderId} ignored — pending sale is $orderId")
                    return
                }
                message.amountCents?.let { RegisterSale.updateAmount(it) }
                Log.d(TAG, "COMPLETE_PAYMENT order=$orderId amount=${RegisterSale.amountCents}")
                listeners.forEach { it.onCompletePayment(orderId, RegisterSale.amountCents) }
            }
            PosMessage.TYPE_CANCEL_PAYMENT -> {
                if (message.orderId != null && message.orderId != RegisterSale.orderId) {
                    Log.d(TAG, "CANCEL_PAYMENT for ${message.orderId} ignored — pending sale is ${RegisterSale.orderId}")
                    return
                }
                Log.d(TAG, "CANCEL_PAYMENT order=${message.orderId} — ${listeners.size} listener(s)")
                RegisterSale.clear()
                lastLaunchOrderId = null // a cancelled order may legitimately retry
                listeners.forEach { it.onCancelPayment(message.orderId) }
            }
        }
    }

    /**
     * Bring the capture screen up from anywhere — including a backgrounded
     * process sitting on the home screen. A plain startActivity() is refused
     * by Android's background-activity-launch rules (verified: BAL_BLOCK), so
     * this fires a high-priority notification with a full-screen intent, the
     * mechanism the platform sanctions for exactly this (incoming calls,
     * alarms): when the screen is on, the system launches the intent's
     * activity immediately. A direct startActivity is still attempted first —
     * it succeeds within the post-foreground grace window and when the overlay
     * appop happens to be granted, which is quicker when it works.
     */
    private fun launchCapture(ctx: Context, biometric: String) {
        // Carry the order straight in the intent so the capture screen never
        // has to depend on the RegisterSale singleton being intact by the time
        // it reads it — a fresh cold-launch, a stale prior sale, or a race with
        // clear() would otherwise leave the amount at 0 and show nothing.
        val intent = android.content.Intent(
            ctx,
            com.bloomingdales.winkpos.WelcomeActivity::class.java,
        )
            .addFlags(
                android.content.Intent.FLAG_ACTIVITY_NEW_TASK or
                    android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP,
            )
            .putExtra(EXTRA_AUTO_BIOMETRIC, biometric)
            .putExtra(EXTRA_ORDER_ID, RegisterSale.orderId)
            .putExtra(EXTRA_AMOUNT_CENTS, RegisterSale.amountCents)

        val orderId = RegisterSale.orderId
        if (orderId != null) handledLaunches.remove(orderId)
        try {
            ctx.startActivity(intent)
        } catch (e: Exception) {
            Log.w(TAG, "direct launch refused (expected when backgrounded): ${e.message}")
        }

        // The full-screen intent exists for when Android silently drops the
        // direct start (background-activity-launch rules, seen on the A380).
        // Where the direct start IS allowed (A3700, Android 11) firing it too
        // re-launches the single-task WelcomeActivity ~200 ms after the SDK's
        // camera activity opened above it — and a single-task relaunch clears
        // everything above it, so the capture died before the customer saw it.
        // So: give the direct start a moment, and post the fallback only if
        // WelcomeActivity never reported handling this order's launch.
        mainHandler.postDelayed({
            if (orderId != null && handledLaunches.contains(orderId)) {
                Log.d(TAG, "direct launch handled for $orderId — no full-screen fallback")
            } else {
                Log.d(TAG, "direct launch not handled for $orderId — posting the full-screen fallback")
                postFullScreenLaunch(ctx, intent)
            }
        }, DIRECT_LAUNCH_GRACE_MS)
    }

    private fun postFullScreenLaunch(ctx: Context, intent: android.content.Intent) {
        // Full-screen intent: reliably launches from the background.
        try {
            val nm = ctx.getSystemService(NotificationManager::class.java)
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
                nm.createNotificationChannel(
                    NotificationChannel(
                        CAPTURE_CHANNEL_ID,
                        "WinkPay payment",
                        NotificationManager.IMPORTANCE_HIGH,
                    ),
                )
            }
            val fsi = PendingIntent.getActivity(
                ctx,
                1,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            val notification = NotificationCompat.Builder(ctx, CAPTURE_CHANNEL_ID)
                .setSmallIcon(com.bloomingdales.winkpos.R.drawable.ic_launcher_bloomies)
                .setContentTitle("WinkPay")
                .setContentText("Starting payment…")
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_CALL)
                .setFullScreenIntent(fsi, true)
                .setAutoCancel(true)
                .setOngoing(false)
                .build()
            nm.notify(CAPTURE_NOTIFICATION_ID, notification)
            // Clear it so it doesn't linger once the activity is up.
            mainHandler.postDelayed({ nm.cancel(CAPTURE_NOTIFICATION_ID) }, 4_000)
        } catch (e: Exception) {
            Log.w(TAG, "full-screen launch failed: ${e.message}")
        }
    }

    /**
     * Follow the terminal's current register. Each register parks its own
     * WebSocket address in PxRetailer when it claims the terminal
     * (RegisterAddress.VARIABLE); when that address changes to one other than
     * the socket we hold, the operator has moved to another PC — drop the
     * socket and connect there. Edge-triggered on the advertised value so an
     * unreachable advertisement (firewall) cannot make us flap: the retry
     * loop's candidate rotation handles that case. Skipped when the operator
     * pinned an address in Settings. Off while a register sale is pending, so
     * a mid-scan switch cannot orphan the capture.
     */
    private fun startRegisterWatch(app: Context, ws: WebSocketTransport) {
        Thread({
            var lastAdvertised: String? = null
            while (true) {
                try { Thread.sleep(REGISTER_WATCH_MS) } catch (_: InterruptedException) { return@Thread }
                if (RegisterAddress.override(app).isNotBlank()) continue
                // Never touch PXRRS while a sale is in progress: the terminal is
                // busy with the capture and every PXRRS call costs it (PAX's
                // logger fails per write); a switch mid-sale is refused anyway.
                if (RegisterSale.isPending) continue
                val advertised = RegisterAddress.discoverFromTerminal(app) ?: continue
                if (advertised == lastAdvertised) continue
                lastAdvertised = advertised
                val current = ws.connectedUrl
                if (current != null && current != advertised && !RegisterSale.isPending) {
                    Log.i(TAG, "terminal is now driven by $advertised (we are on $current) — switching")
                    ws.reconnect("register changed to $advertised")
                }
            }
        }, "RegisterWatch").apply { isDaemon = true }.start()
    }

    private fun onMain(block: () -> Unit) {
        if (Looper.myLooper() == Looper.getMainLooper()) block() else mainHandler.post(block)
    }

    private const val TAG = "PosLink"
    // Rare: a register that loses the terminal now drops our socket, and the
    // reconnect re-discovers the owner. This poll is only the safety net, and
    // every PXRRS call costs the terminal.
    private const val REGISTER_WATCH_MS = 60_000L
    private const val CAPTURE_CHANNEL_ID = "winkpay-capture"
    private const val CAPTURE_NOTIFICATION_ID = 42

    /** Intent extra: "face" | "palm" — launch straight into WinkPay capture. */
    const val EXTRA_AUTO_BIOMETRIC = "autoBiometric"

    /** Intent extras carrying the sale so the amount survives any singleton race. */
    const val EXTRA_ORDER_ID = "registerOrderId"
    const val EXTRA_AMOUNT_CENTS = "registerAmountCents"
}
