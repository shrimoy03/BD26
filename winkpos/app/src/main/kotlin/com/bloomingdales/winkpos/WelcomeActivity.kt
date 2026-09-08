package com.bloomingdales.winkpos

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
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

        // Both modalities use the SDK's full-screen native capture activity.
        // When the register has a sale waiting, a tap is a payment for that
        // amount, not a check-in — otherwise the customer would land in the
        // amount-less enrollment flow and see no total.
        findViewById<View>(R.id.faceTile).setOnClickListener { onBiometricTile("face") }
        findViewById<View>(R.id.palmTile).setOnClickListener { onBiometricTile("palm") }
        findViewById<View>(R.id.settingsButton).setOnClickListener {
            startActivity(SettingsActivity.intent(this))
        }
        findViewById<View>(R.id.signupTile).setOnClickListener {
            statusText.text = getString(R.string.signup_hint)
            statusText.visibility = View.VISIBLE
        }

        if (BuildConfig.WINK_CLIENT_ID.isBlank() || BuildConfig.WINK_MERCHANT_CLIENT_SECRET.isBlank()) {
            statusText.text = getString(R.string.missing_credentials)
            statusText.visibility = View.VISIBLE
        }

        ensureCameraPermission()
        handleAutoBiometric(intent)
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        handleAutoBiometric(intent)
    }

    /**
     * PxRetailer form handoff: the customer tapped Face/Palm on the PAX form
     * (PAYMENTSTATUS FireEvent -> register -> START_PAYMENT method=FACE|PALM),
     * PosLink foregrounded us with the biometric in the intent — go straight
     * into the WinkPay payment for the register's amount, no welcome buttons.
     */
    private fun handleAutoBiometric(intent: android.content.Intent?) {
        val biometric = intent?.getStringExtra(PosLink.EXTRA_AUTO_BIOMETRIC) ?: return
        intent.removeExtra(PosLink.EXTRA_AUTO_BIOMETRIC)

        // Prefer the amount the launch carried; fall back to the live sale.
        val orderId = intent.getStringExtra(PosLink.EXTRA_ORDER_ID)
            ?: PosLink.RegisterSale.orderId
        val amount = intent.getLongExtra(PosLink.EXTRA_AMOUNT_CENTS, 0L)
            .takeIf { it > 0 } ?: PosLink.RegisterSale.amountCents
        intent.removeExtra(PosLink.EXTRA_ORDER_ID)
        intent.removeExtra(PosLink.EXTRA_AMOUNT_CENTS)

        if (orderId == null || amount <= 0) {
            Log.w(TAG, "auto-biometric with no valid sale (orderId=$orderId, amount=$amount) — ignoring")
            return
        }
        startRegisterPayment(biometric, orderId, amount)
    }

    private fun startRegisterPayment(
        biometricType: String,
        orderId: String = PosLink.RegisterSale.orderId ?: "",
        amount: Long = PosLink.RegisterSale.amountCents,
    ) {
        if (checkinInFlight) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED
        ) {
            ensureCameraPermission()
            return
        }
        if (orderId.isBlank() || amount <= 0) {
            Log.w(TAG, "register payment with no amount (orderId=$orderId, amount=$amount) — ignoring")
            return
        }
        Log.d(TAG, "startRegisterPayment $biometricType order=$orderId amount=$amount")
        checkinInFlight = true
        statusText.visibility = View.INVISIBLE

        val tuning = Tuning.load(this)
        val request = com.wink.winkpay.embedded.EmbeddedPaymentRequest(
            requestId = UUID.randomUUID().toString(),
            requestType = if (biometricType == "palm") "PAY_PALM" else "PAY_FACE",
            amount = amount,
            currency = "USD",
            orderId = orderId,
            tenderId = "BD-LOYALLIST",
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

        winkPay.startPayment(request, object : EmbeddedPaymentCallback {
            override fun onSuccess(result: EmbeddedPaymentResult) {
                checkinInFlight = false
                PosLink.sendResult(
                    status = com.bloomingdales.winkpos.link.PosMessage.STATUS_APPROVED,
                    method = "WINK ${biometricType.uppercase()}",
                    token = result.winkToken ?: result.winkCardToken,
                )
                startActivity(
                    ThankYouActivity.intent(
                        this@WelcomeActivity, result.transactionId ?: "", result.amount.toInt(),
                    ),
                )
            }

            override fun onCancelled(requestId: String) {
                checkinInFlight = false
                PosLink.sendResult(status = com.bloomingdales.winkpos.link.PosMessage.STATUS_CANCELLED)
            }

            override fun onFailure(error: EmbeddedPaymentError) {
                checkinInFlight = false
                PosLink.sendResult(
                    status = com.bloomingdales.winkpos.link.PosMessage.STATUS_DECLINED,
                    reason = error.errorMessage.ifBlank { error.errorCode },
                )
                statusText.text = getString(
                    R.string.checkin_failed,
                    error.errorMessage.ifBlank { error.errorCode },
                )
                statusText.visibility = View.VISIBLE
            }
        })

        if (checkinInFlight) {
            startActivity(winkPay.createNativeIntent(this))
        }
    }

    override fun onResume() {
        super.onResume()
        // Session is cleared on Sign Out / after payment, not here — this
        // Activity briefly resumes between the SDK finishing and the
        // Dashboard starting, and must not wipe the just-populated session.
        checkinInFlight = false
        PosLink.addListener(this)
        renderRegisterSale()
    }

    override fun onPause() {
        super.onPause()
        PosLink.removeListener(this)
    }

    // ----- Register link (merchant POS via Wi-Fi or USB/JPxSerialServer) -----

    override fun onStartPayment(orderId: String, amountCents: Long) = renderRegisterSale()

    override fun onCancelPayment(orderId: String?) {
        if (checkinInFlight) winkPay.cancelPayment()
        renderRegisterSale()
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

    /** A tile tap pays the pending register sale, or starts a check-in. */
    private fun onBiometricTile(biometricType: String) {
        if (PosLink.RegisterSale.isPending) {
            startRegisterPayment(biometricType)
        } else {
            startCheckin(biometricType)
        }
    }

    private fun startCheckin(biometricType: String) {
        if (checkinInFlight) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED
        ) {
            ensureCameraPermission()
            return
        }
        checkinInFlight = true
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
            }

            override fun onFailure(error: EmbeddedPaymentError) {
                checkinInFlight = false
                statusText.text = getString(
                    R.string.checkin_failed,
                    error.errorMessage.ifBlank { error.errorCode },
                )
                statusText.visibility = View.VISIBLE
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
    }
}
