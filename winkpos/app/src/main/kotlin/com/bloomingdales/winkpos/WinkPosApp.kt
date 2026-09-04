package com.bloomingdales.winkpos

import android.app.Application
import com.bloomingdales.winkpos.link.PosLink
import com.bloomingdales.winkpos.link.PosLinkService

class WinkPosApp : Application() {
    override fun onCreate() {
        super.onCreate()
        // Register link (WebSocket over Wi-Fi or PCL over USB) — no-op unless
        // POS_LINK_MODE is set in local.properties.
        PosLink.init(this)

        // Pin the process with a foreground service so the link survives the
        // app being backgrounded or the user going Home — otherwise Android
        // freezes the process and the register's launch push never arrives.
        if (PosLink.isEnabled) {
            PosLinkService.start(this)
        }
    }
}
