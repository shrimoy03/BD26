package com.bloomingdales.winkpos

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.widget.SwitchCompat

/**
 * Editable capture-tuning console (gear on the Welcome screen). Values are
 * persisted via [Tuning]; request-level knobs apply to the next check-in and
 * SDK-level knobs are pushed live on Save.
 */
class SettingsActivity : AppCompatActivity() {

    private lateinit var faceRotation: Spinner
    private lateinit var faceCamera: Spinner
    private lateinit var facePreviewRotation: Spinner
    private lateinit var activityOrientationSpinner: Spinner
    private lateinit var minFaceRatio: EditText
    private lateinit var maxFaceRatio: EditText
    private lateinit var faceRatio: EditText
    private lateinit var maxRetries: EditText
    private lateinit var livenessEnabled: SwitchCompat
    private lateinit var palmRotation: Spinner
    private lateinit var palmCamera: Spinner
    private lateinit var palmPreviewRotation: Spinner
    private lateinit var palmFocusMaskOpacity: EditText
    private lateinit var palmTrackingOverlay: SwitchCompat
    private lateinit var palmLivenessEnabled: SwitchCompat
    private lateinit var enableIR: SwitchCompat
    private lateinit var palmMfaEnabled: SwitchCompat
    private lateinit var debugLogging: SwitchCompat

    private val rotations = listOf(0, 90, 180, 270)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        enterKioskMode()

        faceRotation = spinner(R.id.faceRotation, R.array.rotation_options)
        faceCamera = spinner(R.id.faceCamera, R.array.camera_options)
        facePreviewRotation = spinner(R.id.facePreviewRotation, R.array.preview_rotation_options)
        activityOrientationSpinner = spinner(R.id.activityOrientation, R.array.orientation_options)
        palmRotation = spinner(R.id.palmRotation, R.array.palm_rotation_options)
        palmCamera = spinner(R.id.palmCamera, R.array.palm_camera_options)
        palmPreviewRotation = spinner(R.id.palmPreviewRotation, R.array.preview_rotation_options)

        minFaceRatio = findViewById(R.id.minFaceRatio)
        maxFaceRatio = findViewById(R.id.maxFaceRatio)
        faceRatio = findViewById(R.id.faceRatio)
        maxRetries = findViewById(R.id.maxRetries)
        palmFocusMaskOpacity = findViewById(R.id.palmFocusMaskOpacity)
        livenessEnabled = findViewById(R.id.livenessEnabled)
        palmTrackingOverlay = findViewById(R.id.palmTrackingOverlay)
        palmLivenessEnabled = findViewById(R.id.palmLivenessEnabled)
        enableIR = findViewById(R.id.enableIR)
        palmMfaEnabled = findViewById(R.id.palmMfaEnabled)
        debugLogging = findViewById(R.id.debugLogging)

        findViewById<TextView>(R.id.envText).text = "Environment: ${BuildConfig.WINK_ENV}"

        bind(Tuning.load(this))

        findViewById<Button>(R.id.saveButton).setOnClickListener { save() }
        findViewById<Button>(R.id.closeButton).setOnClickListener { finish() }
        findViewById<Button>(R.id.resetButton).setOnClickListener {
            Tuning.reset(this)
            val defaults = Tuning.load(this)
            Tuning.applyLive(defaults)
            bind(defaults)
            Toast.makeText(this, R.string.settings_reset, Toast.LENGTH_SHORT).show()
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) enterKioskMode()
    }

    private fun spinner(id: Int, arrayRes: Int): Spinner {
        val sp = findViewById<Spinner>(id)
        sp.adapter = ArrayAdapter.createFromResource(
            this, arrayRes, android.R.layout.simple_spinner_dropdown_item,
        )
        return sp
    }

    private fun bind(v: Tuning.Values) {
        faceRotation.setSelection(rotations.indexOf(v.faceRotation).coerceAtLeast(0))
        faceCamera.setSelection(v.faceCamera.coerceIn(0, 1))
        // preview_rotation_options: [auto, 0, 90, 180, 270]
        facePreviewRotation.setSelection(
            v.facePreviewRotation?.let { rotations.indexOf(it) + 1 } ?: 0,
        )
        val orientations = resources.getStringArray(R.array.orientation_options)
        activityOrientationSpinner.setSelection(
            orientations.indexOf(v.activityOrientation).coerceAtLeast(0),
        )
        minFaceRatio.setText(v.minFaceRatio?.toString() ?: "")
        maxFaceRatio.setText(v.maxFaceRatio?.toString() ?: "")
        faceRatio.setText(v.faceRatio?.toString() ?: "")
        maxRetries.setText(v.maxRetries.toString())
        livenessEnabled.isChecked = v.livenessEnabled

        // palm_rotation_options: [follow face, 0, 90, 180, 270]
        palmRotation.setSelection(
            if (v.palmRotation == -1) 0 else rotations.indexOf(v.palmRotation) + 1,
        )
        // palm_camera_options: [follow face, 0, 1]
        palmCamera.setSelection(if (v.palmCamera == -1) 0 else v.palmCamera.coerceIn(0, 1) + 1)
        palmPreviewRotation.setSelection(
            v.palmPreviewRotation?.let { rotations.indexOf(it) + 1 } ?: 0,
        )
        palmFocusMaskOpacity.setText(v.palmFocusMaskOpacity?.toString() ?: "")
        palmTrackingOverlay.isChecked = v.palmTrackingOverlay
        palmLivenessEnabled.isChecked = v.palmLivenessEnabled
        enableIR.isChecked = v.enableIR
        palmMfaEnabled.isChecked = v.palmMfaEnabled
        debugLogging.isChecked = v.debugLogging
    }

    private fun save() {
        fun ratio(e: EditText): Double? =
            e.text.toString().trim().toDoubleOrNull()?.takeIf { it in 0.0..1.0 }

        val orientations = resources.getStringArray(R.array.orientation_options)
        val before = Tuning.load(this)
        val v = Tuning.Values(
            faceRotation = rotations[faceRotation.selectedItemPosition],
            faceCamera = faceCamera.selectedItemPosition,
            facePreviewRotation = facePreviewRotation.selectedItemPosition
                .takeIf { it > 0 }?.let { rotations[it - 1] },
            minFaceRatio = ratio(minFaceRatio),
            maxFaceRatio = ratio(maxFaceRatio),
            faceRatio = ratio(faceRatio),
            livenessEnabled = livenessEnabled.isChecked,
            maxRetries = maxRetries.text.toString().trim().toIntOrNull()
                ?.coerceIn(1, 10) ?: 3,
            activityOrientation = orientations[activityOrientationSpinner.selectedItemPosition],
            palmRotation = palmRotation.selectedItemPosition
                .let { if (it == 0) -1 else rotations[it - 1] },
            palmCamera = palmCamera.selectedItemPosition - 1,
            palmPreviewRotation = palmPreviewRotation.selectedItemPosition
                .takeIf { it > 0 }?.let { rotations[it - 1] },
            palmMfaEnabled = palmMfaEnabled.isChecked,
            palmTrackingOverlay = palmTrackingOverlay.isChecked,
            palmLivenessEnabled = palmLivenessEnabled.isChecked,
            enableIR = enableIR.isChecked,
            palmFocusMaskOpacity = palmFocusMaskOpacity.text.toString().trim()
                .toFloatOrNull()?.takeIf { it in 0f..1f },
            debugLogging = debugLogging.isChecked,
        )

        Tuning.save(this, v)
        Tuning.applyLive(v)
        bind(v)

        val msg = if (v.debugLogging != before.debugLogging) {
            R.string.settings_saved_restart
        } else {
            R.string.settings_saved
        }
        Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
    }

    companion object {
        fun intent(context: Context): Intent = Intent(context, SettingsActivity::class.java)
    }
}
