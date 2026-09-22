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

    /** Every PXRRS call costs the terminal dearly (its logger fails per write); do not re-ask within this window. */
    private const val DISCOVERY_TTL_MS = 15_000L
    @Volatile private var lastDiscovered: String? = null
    @Volatile private var lastDiscoveryAt = 0L

    /**
     * This terminal's serial number, as PXRRS reports it in every reply
     * (terminalUptime.terminalSerialNumber). Sent to the register as
     * ?terminal= so a register only ever talks to the app on the terminal it
     * drives; null until the first PXRRS reply has been seen.
     */
    @Volatile var terminalSerial: String? = null
        private set

    /** Registers that refused us as "wrong terminal", with the time the refusal expires. */
    private val rejectedUntil = java.util.concurrent.ConcurrentHashMap<String, Long>()
    private const val REJECT_TTL_MS = 60_000L

    /** A register answered 403: it drives another terminal. Skip it for a while and re-discover. */
    fun markRejected(url: String) {
        rejectedUntil[url] = android.os.SystemClock.elapsedRealtime() + REJECT_TTL_MS
        lastDiscoveryAt = 0L // whatever advertised it is stale — ask the terminal again
    }

    /** The URL to actually dial: the candidate plus this terminal's identity. */
    fun withIdentity(url: String): String {
        val serial = terminalSerial ?: return url
        val sep = if (url.contains('?')) "&" else "?"
        return "$url${sep}terminal=$serial"
    }

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
        val now = android.os.SystemClock.elapsedRealtime()
        rejectedUntil.entries.removeIf { it.value < now }
        return out.filter { !rejectedUntil.containsKey(it) }
    }

    /**
     * Ask the PxRetailer REST service on this very terminal for the address
     * the register published. Blocking (a few hundred ms); call off the main
     * thread. Null when PXRRS is unreachable or nothing is published.
     */
    fun discoverFromTerminal(context: Context): String? {
        val now = android.os.SystemClock.elapsedRealtime()
        if (now - lastDiscoveryAt < DISCOVERY_TTL_MS) return lastDiscovered
        val found = queryTerminal(context)
        lastDiscoveryAt = now
        if (found != lastDiscovered) Log.d(TAG, "register advertises ${found ?: "<nothing>"}")
        lastDiscovered = found
        return found
    }

    private fun queryTerminal(context: Context): String? {
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
                    root.optJSONObject("terminalUptime")?.optString("terminalSerialNumber")
                        ?.takeIf { it.isNotBlank() }?.let { sn ->
                            if (sn != terminalSerial) Log.d(TAG, "this terminal is $sn")
                            terminalSerial = sn
                        }
                    val items = root.optJSONArray("resultItems") ?: return@use
                    for (i in 0 until items.length()) {
                        val item = items.optJSONObject(i) ?: continue
                        if (item.optString("name") != VARIABLE) continue
                        val value = item.optString("value").trim()
                        if (value.startsWith("ws://") || value.startsWith("wss://")) return value
                        return null
                    }
                }
            } catch (e: Exception) {
                Log.d(TAG, "discovery via $url failed: $e", e)
            }
        }
        return null
    }
}
