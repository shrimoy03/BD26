package com.bloomingdales.winkpos.link

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
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
                triggerForm = BuildConfig.POS_BIOMETRIC_TRIGGER_FORM,
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
                // on the PxRetailer form — bring this app to the foreground
                // and go straight into WinkPay capture. Needs the
                // "display over other apps" appop when we're backgrounded:
                //   adb shell appops set com.bloomingdales.winkpos SYSTEM_ALERT_WINDOW allow
                val biometric = message.method?.lowercase()
                if (biometric == "face" || biometric == "palm") {
                    appContext?.let { ctx ->
                        val intent = android.content.Intent(
                            ctx,
                            com.bloomingdales.winkpos.WelcomeActivity::class.java,
                        )
                            .addFlags(
                                android.content.Intent.FLAG_ACTIVITY_NEW_TASK or
                                    android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP,
                            )
                            .putExtra(EXTRA_AUTO_BIOMETRIC, biometric)
                        try {
                            ctx.startActivity(intent)
                        } catch (e: Exception) {
                            Log.w(TAG, "foreground launch failed: ${e.message}")
                        }
                    }
                }
            }
            PosMessage.TYPE_CANCEL_PAYMENT -> {
                if (message.orderId != null && message.orderId != RegisterSale.orderId) return
                RegisterSale.clear()
                listeners.forEach { it.onCancelPayment(message.orderId) }
            }
        }
    }

    private fun onMain(block: () -> Unit) {
        if (Looper.myLooper() == Looper.getMainLooper()) block() else mainHandler.post(block)
    }

    private const val TAG = "PosLink"

    /** Intent extra: "face" | "palm" — launch straight into WinkPay capture. */
    const val EXTRA_AUTO_BIOMETRIC = "autoBiometric"
}
