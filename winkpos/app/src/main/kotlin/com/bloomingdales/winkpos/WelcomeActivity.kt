package com.bloomingdales.winkpos

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.Log
import android.view.View
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.bloomingdales.winkpos.link.PosLink
import com.bloomingdales.winkpos.link.PosLinkService
import com.wink.winkpay.WinkPaySdk
import com.wink.winkpay.embedded.EmbeddedCheckinRequest
import com.wink.winkpay.embedded.EmbeddedCheckinResult
import com.wink.winkpay.embedded.EmbeddedPaymentCallback
import com.wink.winkpay.embedded.EmbeddedPaymentError
import com.wink.winkpay.embedded.EmbeddedPaymentResult
import com.wink.winkpay.embedded.WinkPayEmbedded
import java.util.UUID

/**
 * Bloomingdale's-style welcome / biometric check-in screen.
 * Face and Palm launch the WinkPay SDK in check-in mode; on success the
 * identified customer lands in DashboardActivity.
 */
class WelcomeActivity : AppCompatActivity(), PosLink.Listener {

    private lateinit var winkPay: WinkPayEmbedded
    private lateinit var statusText: TextView
    private var checkinInFlight = false
    private var showingRegisterPrompt = false
    private var lastBiometricType = "face"

    /**
     * True from startCheckin until the SDK reports back (success, cancel or
     * failure). Unlike checkinInFlight it is NOT reset by onResume: while the
     * SDK's capture activity is on top this activity is stopped, and a
     * register cancel or a second launch must still know the camera is held.
     */
    private var captureOpen = false

    /** A launch that arrived while a capture was still open; started once it lets go. */
    private var pendingBiometric: String? = null
    private val handler = Handler(Looper.getMainLooper())
    private val startPendingCapture = Runnable {
        val next = pendingBiometric ?: return@Runnable
        pendingBiometric = null
        captureOpen = false
        checkinInFlight = false
        Log.d(TAG, "previous capture released — starting the queued $next check-in")
        startCheckin(next)
    }

    private val cameraPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (!granted) {
                statusText.text = getString(R.string.camera_permission_needed)
                statusText.visibility = View.VISIBLE
            }
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_welcome)
        enterKioskMode()

        // Guaranteed-foreground moment: pin the register link's process in
        // case the Application-time start was rejected as a background start.
        if (PosLink.isEnabled) {
            PosLinkService.start(this)
        }

        statusText = findViewById(R.id.statusText)

        val tuning = Tuning.load(this)
        winkPay = WinkPayEmbedded.init(
            context = this,
            config = WinkPaySdk.Config(
                clientId = BuildConfig.WINK_CLIENT_ID,
                merchantClientSecret = BuildConfig.WINK_MERCHANT_CLIENT_SECRET,
                deviceSerialNumber = deviceSerial(),
                // Coerce unknown values to "stage" — an unsupported name makes
                // the SDK throw from onCreate.
                sdkEnvironment = BuildConfig.WINK_ENV.lowercase()
                    .takeIf { it in setOf("qa", "stage", "prod") } ?: "stage",
                palmMfaEnabled = tuning.palmMfaEnabled,
                palmTrackingOverlay = tuning.palmTrackingOverlay,
                palmLivenessEnabled = tuning.palmLivenessEnabled,
                enableIR = tuning.enableIR,
                palmFocusMaskOpacity = tuning.palmFocusMaskOpacity,
                debugLogging = tuning.debugLogging,
            ),
        )

        // Idle landing page: the biometric tender is chosen on PxRetailer /
        // the register, which foregrounds this app straight into capture, so
        // there are no on-screen tender buttons. Settings stays reachable, and
        // a long-press on the logo starts a face check-in for bench testing.
        findViewById<View>(R.id.settingsButton).setOnClickListener {
            startActivity(SettingsActivity.intent(this))
        }
        findViewById<View>(R.id.brandLogo).setOnLongClickListener {
            startCheckin("face")
            true
        }

        findViewById<View>(R.id.retryButton).setOnClickListener {
            hideRetryPanel()
            startCheckin(lastBiometricType)
        }
        findViewById<View>(R.id.retryCancelButton).setOnClickListener {
            hideRetryPanel()
            // Tells the register; it returns to checkout with the sale intact.
            PosLink.sendResult(com.bloomingdales.winkpos.link.PosMessage.STATUS_CANCELLED)
            // And put PxRetailer back on the terminal screen right away rather
            // than leaving the customer on this app's idle page.
            PosLink.returnToRetailer()
        }

        if (BuildConfig.WINK_CLIENT_ID.isBlank() || BuildConfig.WINK_MERCHANT_CLIENT_SECRET.isBlank()) {
            statusText.text = getString(R.string.missing_credentials)
            statusText.visibility = View.VISIBLE
        }

        ensureCameraPermission()
        // Listen for the whole lifetime, not just while resumed: the SDK's
        // capture activity sits on top of this one during a scan, and the
        // register's CANCEL_PAYMENT has to reach cancelCaptureIfOpen() then —
        // registering in onResume left the camera running after every cancel,
        // and the next launch then failed at "initializing camera".
        PosLink.addListener(this)
        handleAutoBiometric(intent)
    }

    override fun onDestroy() {
        handler.removeCallbacks(startPendingCapture)
        PosLink.removeListener(this)
        super.onDestroy()
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        handleAutoBiometric(intent)
    }

    /**
     * PxRetailer form handoff: the customer tapped Face/Palm on the PAX form
     * (PAYMENTSTATUS FireEvent -> register -> START_PAYMENT method=FACE|PALM),
     * PosLink foregrounded us with the biometric in the intent.
     *
     * Run check-in, not a headless SDK payment: check-in identifies the
     * customer and lands on the rewards Dashboard, which is already
     * register-aware — it shows the register's amount (held in
     * RegisterSale), the Loyallist points/offers, and its Pay button charges
     * that amount on the saved card and reports the result back to the
     * register. The SDK payment flow would skip all of that and jump to the
     * receipt.
     */
    private fun handleAutoBiometric(intent: android.content.Intent?) {
        val biometric = intent?.getStringExtra(PosLink.EXTRA_AUTO_BIOMETRIC) ?: return
        val intentOrderId = intent.getStringExtra(PosLink.EXTRA_ORDER_ID)
        val intentAmount = intent.getLongExtra(PosLink.EXTRA_AMOUNT_CENTS, 0L)
        intent.removeExtra(PosLink.EXTRA_AUTO_BIOMETRIC)
        intent.removeExtra(PosLink.EXTRA_ORDER_ID)
        intent.removeExtra(PosLink.EXTRA_AMOUNT_CENTS)

        // The intent is the source of truth for the sale: PosLink stamps the
        // order into it so a cold launch through the full-screen intent (the
        // process recreated, RegisterSale empty) or a clear() race still lands
        // the amount. Re-seed the singleton from it — the Dashboard reads the
        // amount from RegisterSale and its Pay button charges that figure.
        if (!intentOrderId.isNullOrBlank() && intentAmount > 0L) {
            PosLink.RegisterSale.set(intentOrderId, intentAmount)
        }
        val orderId = PosLink.RegisterSale.orderId
        val amount = PosLink.RegisterSale.amountCents
        if (amount <= 0L) {
            Log.w(TAG, "auto check-in ($biometric) but no register amount — intent=$intentAmount, RegisterSale=$amount")
        }
        Log.d(TAG, "auto check-in ($biometric) for register sale order=$orderId amount=$amount")
        startCheckin(biometric)
    }

    override fun onResume() {
        super.onResume()
        // Session is cleared on Sign Out / after payment, not here — this
        // Activity briefly resumes between the SDK finishing and the
        // Dashboard starting, and must not wipe the just-populated session.
        checkinInFlight = false
        renderRegisterSale()
    }

    // ----- Register link (merchant POS via Wi-Fi or USB/JPxSerialServer) -----

    override fun onStartPayment(orderId: String, amountCents: Long) {
        hideRetryPanel()
        renderRegisterSale()
    }

    override fun onCancelPayment(orderId: String?) {
        pendingBiometric = null
        handler.removeCallbacks(startPendingCapture)
        cancelCaptureIfOpen("register cancelled the sale")
        hideRetryPanel()
        renderRegisterSale()
    }

    /**
     * Tear down an in-flight SDK capture so it releases the camera. The SDK
     * answers through onCancelled (not always — see startPendingCapture's
     * timer fallback), which is where captureOpen is cleared.
     */
    private fun cancelCaptureIfOpen(why: String): Boolean {
        if (!captureOpen) return false
        Log.d(TAG, "cancelling the open capture — $why")
        try {
            winkPay.cancelPayment()
        } catch (e: Exception) {
            Log.w(TAG, "cancelPayment failed: ${e.message}")
        }
        checkinInFlight = false
        return true
    }

    /**
     * A register-driven scan that fails or is backed out of must offer the
     * customer a way forward — without this the idle page just sits there
     * while the register waits.
     */
    private fun showRetryPanel(message: String) {
        findViewById<android.widget.TextView>(R.id.retryMessage).text = message
        findViewById<View>(R.id.retryPanel).visibility = View.VISIBLE
    }

    private fun hideRetryPanel() {
        findViewById<View>(R.id.retryPanel).visibility = View.GONE
    }

    /** The idle screen doubles as the "pay $X" prompt for register-driven sales. */
    private fun renderRegisterSale() {
        val sale = PosLink.RegisterSale
        if (sale.isPending) {
            showingRegisterPrompt = true
            statusText.text = getString(
                R.string.register_sale_prompt,
                String.format(java.util.Locale.US, "$%,.2f", sale.amountCents / 100.0),
            )
            statusText.visibility = View.VISIBLE
        } else if (showingRegisterPrompt) {
            showingRegisterPrompt = false
            statusText.visibility = View.INVISIBLE
        }
    }

    private fun ensureCameraPermission() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED
        ) {
            cameraPermission.launch(Manifest.permission.CAMERA)
        }
    }

    private fun startCheckin(biometricType: String) {
        // A previous capture still owns the camera (cancelled by the register
        // while its activity was on top, or abandoned). Starting a second one
        // over it stalls at "initializing camera" and fails. Cancel it and
        // start this one once it reports back — or after a short grace period
        // if the SDK never does.
        if (captureOpen) {
            Log.d(TAG, "capture still open — cancelling it before the $biometricType check-in")
            pendingBiometric = biometricType
            cancelCaptureIfOpen("superseded by a new launch")
            handler.removeCallbacks(startPendingCapture)
            handler.postDelayed(startPendingCapture, CAPTURE_RELEASE_GRACE_MS)
            return
        }
        if (checkinInFlight) return
        lastBiometricType = biometricType
        hideRetryPanel()
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED
        ) {
            ensureCameraPermission()
            return
        }
        checkinInFlight = true
        captureOpen = true
        statusText.visibility = View.INVISIBLE

        val tuning = Tuning.load(this)
        val request = EmbeddedCheckinRequest(
            requestId = UUID.randomUUID().toString(),
            biometricType = biometricType,
            returnOnFailure = true,
            rotationAngle = tuning.faceRotation,
            cameraIndex = tuning.faceCamera,
            previewRotation = tuning.facePreviewRotation,
            minFaceRatio = tuning.minFaceRatio,
            maxFaceRatio = tuning.maxFaceRatio,
            faceRatio = tuning.faceRatio,
            livenessEnabled = tuning.livenessEnabled,
            maxRetries = tuning.maxRetries,
            activityOrientation = Tuning.orientationConstant(tuning.activityOrientation),
            palmRotationAngle = tuning.palmRotation,
            palmCameraIndex = tuning.palmCamera,
            palmPreviewRotation = tuning.palmPreviewRotation,
        )

        winkPay.startCheckin(request, object : EmbeddedPaymentCallback {
            override fun onCheckinSuccess(result: EmbeddedCheckinResult) {
                checkinInFlight = false
                captureOpen = false
                try {
                    CheckinSession.populate(
                        winkTag = result.winkTag,
                        accessToken = result.accessToken,
                        loginResponseJson = result.loginResponseJson,
                    )
                } catch (e: Exception) {
                    statusText.text = getString(
                        R.string.checkin_failed,
                        e.message ?: "unexpected server response",
                    )
                    statusText.visibility = View.VISIBLE
                    return
                }
                startActivity(DashboardActivity.intent(this@WelcomeActivity))
            }

            override fun onSuccess(result: EmbeddedPaymentResult) = Unit

            override fun onCancelled(requestId: String) {
                checkinInFlight = false
                captureOpen = false
                if (pendingBiometric != null) {
                    // Cancelled to make room for a newer launch: start it now
                    // rather than waiting out the grace timer.
                    handler.removeCallbacks(startPendingCapture)
                    startPendingCapture.run()
                    return
                }
                if (PosLink.RegisterSale.isPending) {
                    showRetryPanel(getString(R.string.scan_cancelled_message))
                }
            }

            override fun onFailure(error: EmbeddedPaymentError) {
                checkinInFlight = false
                captureOpen = false
                val message = getString(
                    R.string.checkin_failed,
                    error.errorMessage.ifBlank { error.errorCode },
                )
                if (PosLink.RegisterSale.isPending) {
                    // Register sale in flight: give the customer a way forward
                    // instead of stranding them on the idle page.
                    showRetryPanel(message)
                } else {
                    statusText.text = message
                    statusText.visibility = View.VISIBLE
                }
            }
        })

        // Some failures (e.g. PALM_UNSUPPORTED) are delivered synchronously via
        // onFailure before startCheckin returns — don't launch the capture UI
        // over the error in that case.
        if (checkinInFlight) {
            // Single-display kiosk: launch the SDK's capture UI on this display.
            startActivity(winkPay.createNativeIntent(this))
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) enterKioskMode()
    }

    private fun deviceSerial(): String =
        Settings.Secure.getString(contentResolver, Settings.Secure.ANDROID_ID)
            ?: "winkpos-demo-device"

    private companion object {
        const val TAG = "WelcomeActivity"

        /** How long to wait for a cancelled capture to report back before starting the next one anyway. */
        const val CAPTURE_RELEASE_GRACE_MS = 1_500L
    }
}
