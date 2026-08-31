package com.bloomingdales.winkpos

import android.app.Application
import com.bloomingdales.winkpos.link.PosLink

class WinkPosApp : Application() {
    override fun onCreate() {
        super.onCreate()
        // Register link (WebSocket over Wi-Fi or PCL over USB) — no-op unless
        // POS_LINK_MODE is set in local.properties.
        PosLink.init(this)
    }
}
