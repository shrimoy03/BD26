package com.bloomingdales.winkpos

import android.content.Context
import android.content.pm.ActivityInfo
import com.wink.winkpay.WinkPaySdk

/**
 * Capture tuning knobs, ported from the Clover pilot app's field settings
 * (CloverPilotTestApp FlowSettings/SdkBootstrap). Values are editable from
 * the gear on the Welcome screen and persisted in shared prefs; the defaults
 * below are the field-proven pilot values.
 *
 * Request-level knobs are read fresh on every check-in. SDK-level knobs are
 * pushed live onto [WinkPaySdk]'s runtime properties via [applyLive] —
 * except [Values.debugLogging], which only takes effect on the next app
 * launch (it is read once at SDK init).
 */
object Tuning {

    private const val PREFS = "capture_tuning"

    data class Values(
        // — Face (per-request) —
        val faceRotation: Int = 0,             // 0/90/180/270 pose hint
        val faceCamera: Int = 0,               // 0 = front, 1 = back
        val facePreviewRotation: Int? = null,  // null = camera default
        val minFaceRatio: Double? = null,      // null = device-class default
        val maxFaceRatio: Double? = null,      // null = SDK default
        val faceRatio: Double? = null,         // null = derived
        val livenessEnabled: Boolean = true,
        val maxRetries: Int = 3,
        val activityOrientation: String = "auto",
        // — Palm (per-request overrides) —
        val palmRotation: Int = 90,            // pilot field value; -1 = follow face
        val palmCamera: Int = -1,              // -1 = follow face
        val palmPreviewRotation: Int? = null,  // null = follow face
        // — SDK-level —
        val palmMfaEnabled: Boolean = true,
        val palmTrackingOverlay: Boolean = true,
        val palmLivenessEnabled: Boolean = true,
        val enableIR: Boolean = false,
        val palmFocusMaskOpacity: Float? = null, // null = adaptive
        val debugLogging: Boolean = false,       // needs app relaunch
    )

    fun load(context: Context): Values {
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val d = Values()
        fun s(key: String): String? = p.getString(key, null)
        return Values(
            faceRotation = s("faceRotation")?.toIntOrNull() ?: d.faceRotation,
            faceCamera = s("faceCamera")?.toIntOrNull() ?: d.faceCamera,
            facePreviewRotation = s("facePreviewRotation")?.toIntOrNull(),
            minFaceRatio = s("minFaceRatio")?.toDoubleOrNull(),
            maxFaceRatio = s("maxFaceRatio")?.toDoubleOrNull(),
            faceRatio = s("faceRatio")?.toDoubleOrNull(),
            livenessEnabled = p.getBoolean("livenessEnabled", d.livenessEnabled),
            maxRetries = s("maxRetries")?.toIntOrNull() ?: d.maxRetries,
            activityOrientation = s("activityOrientation") ?: d.activityOrientation,
            palmRotation = s("palmRotation")?.toIntOrNull() ?: d.palmRotation,
            palmCamera = s("palmCamera")?.toIntOrNull() ?: d.palmCamera,
            palmPreviewRotation = s("palmPreviewRotation")?.toIntOrNull(),
            palmMfaEnabled = p.getBoolean("palmMfaEnabled", d.palmMfaEnabled),
            palmTrackingOverlay = p.getBoolean("palmTrackingOverlay", d.palmTrackingOverlay),
            palmLivenessEnabled = p.getBoolean("palmLivenessEnabled", d.palmLivenessEnabled),
            enableIR = p.getBoolean("enableIR", d.enableIR),
            palmFocusMaskOpacity = s("palmFocusMaskOpacity")?.toFloatOrNull(),
            debugLogging = p.getBoolean("debugLogging", d.debugLogging),
        )
    }

    fun save(context: Context, v: Values) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().apply {
            putString("faceRotation", v.faceRotation.toString())
            putString("faceCamera", v.faceCamera.toString())
            putString("facePreviewRotation", v.facePreviewRotation?.toString())
            putString("minFaceRatio", v.minFaceRatio?.toString())
            putString("maxFaceRatio", v.maxFaceRatio?.toString())
            putString("faceRatio", v.faceRatio?.toString())
            putBoolean("livenessEnabled", v.livenessEnabled)
            putString("maxRetries", v.maxRetries.toString())
            putString("activityOrientation", v.activityOrientation)
            putString("palmRotation", v.palmRotation.toString())
            putString("palmCamera", v.palmCamera.toString())
            putString("palmPreviewRotation", v.palmPreviewRotation?.toString())
            putBoolean("palmMfaEnabled", v.palmMfaEnabled)
            putBoolean("palmTrackingOverlay", v.palmTrackingOverlay)
            putBoolean("palmLivenessEnabled", v.palmLivenessEnabled)
            putBoolean("enableIR", v.enableIR)
            putString("palmFocusMaskOpacity", v.palmFocusMaskOpacity?.toString())
            putBoolean("debugLogging", v.debugLogging)
        }.apply()
    }

    fun reset(context: Context) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().clear().apply()
    }

    /** Push the SDK-level knobs onto the live SDK singleton. */
    fun applyLive(v: Values) {
        WinkPaySdk.palmMfaEnabled = v.palmMfaEnabled
        WinkPaySdk.palmTrackingOverlay = v.palmTrackingOverlay
        WinkPaySdk.palmLivenessEnabled = v.palmLivenessEnabled
        WinkPaySdk.enableIR = v.enableIR
        WinkPaySdk.palmFocusMaskOpacity = v.palmFocusMaskOpacity
    }

    fun orientationConstant(name: String): Int? = when (name) {
        "landscape" -> ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
        "portrait" -> ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        "sensorLandscape" -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        "reverseLandscape" -> ActivityInfo.SCREEN_ORIENTATION_REVERSE_LANDSCAPE
        else -> null
    }
}
