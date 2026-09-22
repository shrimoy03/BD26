package com.bloomingdales.winkpos.link

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/**
 * Brings the register link up without anyone touching the terminal.
 *
 * On boot, and after this APK is updated in place, start the foreground
 * service: that alone creates the process, runs [com.bloomingdales.winkpos.WinkPosApp]
 * (which initialises [PosLink]), discovers the register through the terminal's
 * PXRRS and connects. No screen is shown until a sale arrives — then the
 * existing full-screen launch path opens the capture from the background.
 *
 * Android caveat: a freshly installed app is in the "stopped" state and gets
 * no broadcasts until it has been launched once by hand. So the very first
 * time on a new terminal, tap the icon once; every reboot and update after
 * that is automatic. Starting a foreground service from BOOT_COMPLETED and
 * MY_PACKAGE_REPLACED is exempt from the background-start restrictions.
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED,
            Intent.ACTION_LOCKED_BOOT_COMPLETED,
            Intent.ACTION_MY_PACKAGE_REPLACED,
            "android.intent.action.QUICKBOOT_POWERON" -> {
                Log.i(TAG, "${intent.action} — starting the register link service")
                if (PosLink.isEnabled) PosLinkService.start(context)
            }
        }
    }

    private companion object {
        const val TAG = "BootReceiver"
    }
}
