package com.bloomingdales.winkpos

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import android.provider.Settings
import android.view.View
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
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
class WelcomeActivity : AppCompatActivity() {

    private lateinit var winkPay: WinkPayEmbedded
    private lateinit var statusText: TextView
    private var checkinInFlight = false

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
        findViewById<View>(R.id.faceTile).setOnClickListener { startCheckin("face") }
        findViewById<View>(R.id.palmTile).setOnClickListener { startCheckin("palm") }
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
    }

    override fun onResume() {
        super.onResume()
        // Session is cleared on Sign Out / after payment, not here — this
        // Activity briefly resumes between the SDK finishing and the
        // Dashboard starting, and must not wipe the just-populated session.
        checkinInFlight = false
    }

    private fun ensureCameraPermission() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED
        ) {
            cameraPermission.launch(Manifest.permission.CAMERA)
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
}
