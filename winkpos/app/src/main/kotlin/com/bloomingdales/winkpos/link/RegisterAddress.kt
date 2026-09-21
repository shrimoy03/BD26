package com.bloomingdales.winkpos.link

import android.content.Context
import android.util.Log
import com.bloomingdales.winkpos.BuildConfig
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject

/**
 * Where the register's WebSocket server is. Resolved, in order:
 *
 *  1. an operator override from the Settings screen (shared prefs),
 *  2. the address the register itself parked in PxRetailer's
 *     [VARIABLE] (the register writes `ws://<its-ip>:8181/pos` there on every
 *     subscribe — see windowsterminal JpxRestLink.PublishRegisterAddressAsync),
 *  3. the last address that actually connected,
 *  4. the compiled-in POS_LINK_WS_URL.
 *
 * (2) is what makes a fresh terminal find whichever PC is driving it: the
 * compiled-in address is the dev Mac's, and on any other register the face
 * handoff used to background PxRetailer while the launch went to the wrong
 * machine — WinkPay came up and nothing started.
 */
object RegisterAddress {
    private const val TAG = "RegisterAddress"
    private const val PREFS = "register_link"
    private const val KEY_OVERRIDE = "wsUrlOverride"
    private const val KEY_LAST_GOOD = "wsUrlLastGood"
    const val VARIABLE = "STR.TEXT_12"

    fun override(context: Context): String =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_OVERRIDE, "").orEmpty()

    fun setOverride(context: Context, url: String) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY_OVERRIDE, url.trim()).apply()
    }

    fun rememberGood(context: Context, url: String) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY_LAST_GOOD, url).apply()
    }

    /** Candidate URLs in priority order, de-duplicated; never empty when a default is compiled in. */
    fun candidates(context: Context): List<String> {
        val out = LinkedHashSet<String>()
        override(context).takeIf { it.isNotBlank() }?.let { out += it }
        discoverFromTerminal(context)?.let { out += it }
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY_LAST_GOOD, null)?.takeIf { it.isNotBlank() }?.let { out += it }
        BuildConfig.POS_LINK_WS_URL.takeIf { it.isNotBlank() }?.let { out += it }
        return out.toList()
    }

    /**
     * Ask the PxRetailer REST service on this very terminal for the address
     * the register published. Blocking (a few hundred ms); call off the main
     * thread. Null when PXRRS is unreachable or nothing is published.
     */
    fun discoverFromTerminal(context: Context): String? {
        val base = BuildConfig.POS_LINK_PXRRS_URL.ifBlank { PxrrsTransport.DEFAULT_URL }.trimEnd('/')
        val client = PxrrsTransport.buildClient(base, context)
        for (url in PxrrsTransport.candidateUrlsFor(base)) {
            try {
                client.newCall(
                    Request.Builder()
                        .url("$url/getVariable?variableNames=$VARIABLE")
                        .post("".toRequestBody(PxrrsTransport.JSON))
                        .build(),
                ).execute().use { response ->
                    val root = JSONObject(response.body?.string().orEmpty())
                    val items = root.optJSONArray("resultItems") ?: return@use
                    for (i in 0 until items.length()) {
                        val item = items.optJSONObject(i) ?: continue
                        if (item.optString("name") != VARIABLE) continue
                        val value = item.optString("value").trim()
                        if (value.startsWith("ws://") || value.startsWith("wss://")) {
                            Log.d(TAG, "register advertises $value (via $url)")
                            return value
                        }
                        return null
                    }
                }
            } catch (e: Exception) {
                Log.d(TAG, "discovery via $url failed: ${e.message}")
            }
        }
        return null
    }
}
