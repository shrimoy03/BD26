package com.bloomingdales.winkpos

import android.app.Activity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat

/**
 * Immersive kiosk mode: hides status + navigation bars while this app's
 * screens are visible. Swiping from an edge shows the bars transiently.
 * Call from onCreate and onWindowFocusChanged(hasFocus = true) so the bars
 * stay hidden after dialogs/SDK screens return focus.
 */
fun Activity.enterKioskMode() {
    WindowCompat.setDecorFitsSystemWindows(window, false)
    WindowInsetsControllerCompat(window, window.decorView).apply {
        systemBarsBehavior =
            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        hide(WindowInsetsCompat.Type.systemBars())
    }
}
