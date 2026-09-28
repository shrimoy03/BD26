package com.bloomingdales.winkpos.link

import android.content.Context
import android.net.ConnectivityManager
import android.net.wifi.WifiManager
import android.os.Build
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetSocketAddress
import java.net.NetworkInterface

/**
 * Answers the register's "who is out there" probe so nobody has to read an
 * address off the terminal.
 *
 * The register (windowsterminal Services/TerminalDiscovery.cs) sends
 * `WINKPOS_DISCOVER 1` to the IPv4 broadcast address and to the IPv6
 * all-nodes group on UDP [PORT]; this replies to the sender with the
 * terminal's serial, model and addresses. The register uses the reply's
 * source address to learn which family actually routes — on a phone hotspot
 * with an IPv6-only carrier, IPv4 between clients is dead (each gets a
 * private 192.0.0.x/32 translator address) and only the hotspot's IPv6 /64
 * works.
 *
 * IPv6 addresses are listed stable-first and without deprecated ones, so the
 * register picks an address the terminal keeps for the day rather than a
 * privacy address that rotates.
 */
object DiscoveryResponder {
    const val PORT = 8182
    private const val PROBE = "WINKPOS_DISCOVER"
    private const val TAG = "Discovery"

    // IFA_F_TEMPORARY / IFA_F_DEPRECATED from linux/if_addr.h (LinkAddress.getFlags()).
    private const val IFA_F_TEMPORARY = 0x01
    private const val IFA_F_DEPRECATED = 0x20

    @Volatile private var thread: Thread? = null
    @Volatile private var socket: DatagramSocket? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    fun start(context: Context) {
        if (thread != null) return
        val app = context.applicationContext
        try {
            // Wi-Fi drivers drop multicast/broadcast unless someone holds this.
            val wifi = app.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            multicastLock = wifi?.createMulticastLock("winkpos-discovery")?.apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (e: Exception) {
            Log.w(TAG, "multicast lock unavailable: ${e.message}")
        }
        val t = Thread({ serve(app) }, "winkpos-discovery").apply { isDaemon = true }
        thread = t
        t.start()
    }

    fun stop() {
        thread = null
        try { socket?.close() } catch (_: Exception) {}
        socket = null
        try { multicastLock?.release() } catch (_: Exception) {}
        multicastLock = null
    }

    private fun serve(app: Context) {
        val buf = ByteArray(1024)
        while (thread != null) {
            try {
                // Wildcard bind is dual-stack on Android: IPv4 broadcasts and
                // IPv6 all-nodes multicasts both land here.
                val s = DatagramSocket(null).apply {
                    reuseAddress = true
                    broadcast = true
                    bind(InetSocketAddress(PORT))
                }
                socket = s
                Log.i(TAG, "discovery responder listening on udp/$PORT")
                while (thread != null) {
                    val packet = DatagramPacket(buf, buf.size)
                    s.receive(packet)
                    val text = String(packet.data, packet.offset, packet.length, Charsets.UTF_8).trim()
                    if (!text.startsWith(PROBE)) continue
                    val reply = buildReply(app).toString().toByteArray(Charsets.UTF_8)
                    s.send(DatagramPacket(reply, reply.size, packet.socketAddress))
                    Log.d(TAG, "answered probe from ${packet.socketAddress}")
                }
            } catch (e: Exception) {
                if (thread == null) return
                Log.w(TAG, "responder restarting: ${e.message}")
                try { Thread.sleep(3_000) } catch (_: InterruptedException) { return }
            } finally {
                try { socket?.close() } catch (_: Exception) {}
                socket = null
            }
        }
    }

    private fun buildReply(app: Context): JSONObject {
        val v4 = JSONArray()
        val v6Stable = mutableListOf<String>()
        val v6Temp = mutableListOf<String>()

        var fromLinkProperties = false
        try {
            val cm = app.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            val lp = cm.activeNetwork?.let { cm.getLinkProperties(it) }
            if (lp != null) {
                fromLinkProperties = true
                for (la in lp.linkAddresses) {
                    val a = la.address
                    val flags = la.flags
                    if (flags and IFA_F_DEPRECATED != 0) continue
                    when {
                        a is Inet4Address && !a.isLoopbackAddress && !a.isLinkLocalAddress -> v4.put(a.hostAddress)
                        a is Inet6Address && isGlobal(a) ->
                            (if (flags and IFA_F_TEMPORARY != 0) v6Temp else v6Stable).add(plain(a))
                    }
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "link properties unavailable: ${e.message}")
        }
        if (!fromLinkProperties) {
            try {
                for (nic in NetworkInterface.getNetworkInterfaces().toList()) {
                    if (!nic.isUp || nic.isLoopback) continue
                    for (a in nic.inetAddresses.toList()) {
                        when {
                            a is Inet4Address && !a.isLoopbackAddress && !a.isLinkLocalAddress -> v4.put(a.hostAddress)
                            a is Inet6Address && isGlobal(a) -> v6Stable.add(plain(a))
                        }
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "interface scan failed: ${e.message}")
            }
        }

        val v6 = JSONArray().also { arr -> (v6Stable + v6Temp).forEach { arr.put(it) } }
        val serial = RegisterAddress.terminalSerial ?: try {
            @Suppress("DEPRECATION")
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) Build.getSerial() else Build.SERIAL
        } catch (_: Exception) { "unknown" }
        return JSONObject()
            .put("serial", serial)
            .put("model", Build.MODEL ?: "PAX")
            .put("ipv4", v4)
            .put("ipv6", v6)
            .put("wsPort", 0) // reserved
    }

    private fun isGlobal(a: Inet6Address): Boolean =
        !a.isLoopbackAddress && !a.isLinkLocalAddress && !a.isMulticastAddress && !a.isSiteLocalAddress

    /** Host address without the "%wlan0" zone suffix Java appends. */
    private fun plain(a: Inet6Address): String = (a.hostAddress ?: "").substringBefore('%')
}
