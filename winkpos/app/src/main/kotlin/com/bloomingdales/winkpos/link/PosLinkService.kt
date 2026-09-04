package com.bloomingdales.winkpos.link

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat
import com.bloomingdales.winkpos.R
import com.bloomingdales.winkpos.WelcomeActivity

/**
 * Foreground service that pins this app's process — and with it the PosLink
 * transport socket — while the app is backgrounded or the user is on the home
 * screen. Without it Android caches/freezes the process minutes after it
 * leaves the foreground, the WebSocket dies, and the register's
 * START_PAYMENT push has nobody to land on.
 *
 * Holds no logic of its own: PosLink is initialized by the Application class,
 * this just keeps the process alive. START_STICKY brings the service (and the
 * Application, and therefore the link) back if the process is ever killed.
 */
class PosLinkService : Service() {

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        val notification = buildNotification()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(
                NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE,
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    private fun buildNotification(): Notification {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            getSystemService(NotificationManager::class.java).createNotificationChannel(
                NotificationChannel(
                    CHANNEL_ID,
                    "Register link",
                    NotificationManager.IMPORTANCE_MIN,
                ),
            )
        }
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, WelcomeActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher_bloomies)
            .setContentTitle("WinkPay register link")
            .setContentText("Listening for the register")
            .setOngoing(true)
            .setContentIntent(open)
            .build()
    }

    companion object {
        private const val TAG = "PosLinkService"
        private const val CHANNEL_ID = "pos-link"
        private const val NOTIFICATION_ID = 41

        /**
         * Best-effort start: allowed whenever the app is (about to be) in the
         * foreground; Android 12+ throws if attempted from a true background
         * start, in which case the next activity launch pins it instead.
         */
        fun start(context: Context) {
            val intent = Intent(context, PosLinkService::class.java)
            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    context.startForegroundService(intent)
                } else {
                    context.startService(intent)
                }
            } catch (e: Exception) {
                Log.w(TAG, "could not start the link service yet: ${e.message}")
            }
        }
    }
}
