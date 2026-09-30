package com.bloomingdales.winkpos.link

import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.util.Log

/**
 * Turns off PxRetailerRestService's file logging from this side.
 *
 * PXRRS (1.16.42 and 1.16.43_T alike) starts its PAX logger at DEBUG and
 * writes every line to /sdcard/Logger/<pkg>/log. On Android 11+ that
 * directory is off-limits to it (scoped storage, no MANAGE_EXTERNAL_STORAGE),
 * so every single write fails through MediaProvider at ~40 ms a piece — a
 * queue that runs continuously, holds the process at 10–20 % CPU while idle,
 * and stretches a 100 ms REST call to 1.5–3 s whenever the register sends a
 * burst. Measured on the A380: bursts went from 1.7–2.1 s per call to
 * 0.10–0.20 s the moment the level was set to NONE.
 *
 * The logger library registers a runtime receiver for two broadcasts
 * (com.pax.logger.util.LoggerStatusTool / LoggerBroadcastReceive):
 *   com.pax.action.LOGGER_MESSAGE  extra "level" -> LogUtil.setLogLevel
 *   com.pax.logger.action          extra "state" -> LogUtil.setLogEnable (logcat)
 * Both are in-memory, and PXRRS resets to DEBUG on every start, so this is
 * sent on service start and then every 15 s — cheap, idempotent, and a
 * no-op when PXRRS is not running.
 */
object PxrrsLogQuieter {
    private const val TAG = "PxrrsLogQuieter"
    private const val PXRRS_PACKAGE = "com.pax.multilane.pxretailerrestservice"
    private const val PERIOD_MS = 15_000L

    private val handler = Handler(Looper.getMainLooper())
    private var running = false
    private var sent = 0

    fun start(context: Context) {
        if (running) return
        running = true
        val app = context.applicationContext
        val tick = object : Runnable {
            override fun run() {
                if (!running) return
                quiet(app)
                handler.postDelayed(this, PERIOD_MS)
            }
        }
        handler.post(tick)
    }

    fun stop() {
        running = false
        handler.removeCallbacksAndMessages(null)
    }

    /** One-shot: also called right after a sale hands over, when PXRRS is busiest. */
    fun quiet(context: Context) {
        try {
            context.sendBroadcast(
                Intent("com.pax.action.LOGGER_MESSAGE")
                    .setPackage(PXRRS_PACKAGE)
                    .putExtra("level", "NONE"),
            )
            context.sendBroadcast(
                Intent("com.pax.logger.action")
                    .setPackage(PXRRS_PACKAGE)
                    .putExtra("state", false),
            )
            if (sent++ == 0) Log.i(TAG, "asked PXRRS to stop file logging (level NONE, logcat off); repeating every ${PERIOD_MS / 1000}s")
        } catch (e: Exception) {
            Log.w(TAG, "could not send logger broadcast: ${e.message}")
        }
    }
}
