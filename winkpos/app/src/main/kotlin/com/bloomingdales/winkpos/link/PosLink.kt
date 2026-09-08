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
    }

    /** The sale the register asked us to collect; null orderId = no pending sale. */
    object RegisterSale {
        @Volatile var orderId: String? = null
            private set
        @Volatile var amountCents: Long = 0
            private set

        val isPending: Boolean get() = orderId != null

        internal fun set(orderId: String, amountCents: Long) {
            this.orderId = orderId
            this.amountCents = amountCents
        }

        internal fun clear() {
            orderId = null
            amountCents = 0
        }
    }

    @Volatile var isConnected: Boolean = false
        private set
    val isEnabled: Boolean get() = transport != null

    private var transport: PosLinkTransport? = null
    private val listeners = CopyOnWriteArrayList<Listener>()
    private val mainHandler = Handler(Looper.getMainLooper())
    private var appContext: Context? = null

    fun init(context: Context) {
        appContext = context.applicationContext
        if (transport != null) return
        transport = when (BuildConfig.POS_LINK_MODE) {
            "ws" -> {
                val url = BuildConfig.POS_LINK_WS_URL
                if (url.isBlank()) {
                    Log.w(TAG, "POS_LINK_MODE=ws but POS_LINK_WS_URL is not set — link disabled")
                    null
                } else {
                    WebSocketTransport(url)
                }
            }
            // PXRRS requires HTTPS with a client certificate even on loopback,
            // so the default is https:// and the transport needs a Context to
            // read the bundled keystore out of assets.
            "pxrrs" -> PxrrsTransport(
                BuildConfig.POS_LINK_PXRRS_URL.ifBlank { "https://127.0.0.1:9090" },
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

    /** Report the outcome of the pending register sale and clear it. */
    fun sendResult(
        status: String,
        method: String? = null,
        reason: String? = null,
        token: String? = null,
    ) {
        val orderId = RegisterSale.orderId ?: return
        val amount = RegisterSale.amountCents
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
            ),
        )
    }

    private fun dispatch(message: PosMessage) {
        when (message.type) {
            PosMessage.TYPE_START_PAYMENT -> {
                val orderId = message.orderId ?: return
                val amount = message.amountCents ?: return
                RegisterSale.set(orderId, amount)
                listeners.forEach { it.onStartPayment(orderId, amount) }

                // FACE/PALM = the customer already picked a biometric tender
                // on the PxRetailer form — bring this app to the foreground and
                // go straight into WinkPay capture, even from the background.
                val biometric = message.method?.lowercase()
                if (biometric == "face" || biometric == "palm") {
                    appContext?.let { ctx -> launchCapture(ctx, biometric) }
                }
            }
            PosMessage.TYPE_CANCEL_PAYMENT -> {
                if (message.orderId != null && message.orderId != RegisterSale.orderId) return
                RegisterSale.clear()
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

        try {
            ctx.startActivity(intent)
        } catch (e: Exception) {
            Log.w(TAG, "direct launch refused (expected when backgrounded): ${e.message}")
        }

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

    private fun onMain(block: () -> Unit) {
        if (Looper.myLooper() == Looper.getMainLooper()) block() else mainHandler.post(block)
    }

    private const val TAG = "PosLink"
    private const val CAPTURE_CHANNEL_ID = "winkpay-capture"
    private const val CAPTURE_NOTIFICATION_ID = 42

    /** Intent extra: "face" | "palm" — launch straight into WinkPay capture. */
    const val EXTRA_AUTO_BIOMETRIC = "autoBiometric"

    /** Intent extras carrying the sale so the amount survives any singleton race. */
    const val EXTRA_ORDER_ID = "registerOrderId"
    const val EXTRA_AMOUNT_CENTS = "registerAmountCents"
}
