package com.bloomingdales.winkpos.link

import android.util.Log

/**
 * USB transport: exchanges PCL frames with JPxSerialServer on the register
 * PC through the device's serial port. The register talks to JPxSerialServer
 * either via its raw PCL TCP socket (serverSocket.port=7001) or its REST
 * API; either way what reaches this device over USB is PCL frames.
 *
 * Frame handling notes (pending the PCL spec from PAX — see PclFrameCodec):
 * we ACK every CRC-valid frame and treat our own sends as fire-and-forget.
 */
class PclSerialTransport(
    private val io: SerialIo,
    private val sendAcks: Boolean = true,
) : PosLinkTransport {

    @Volatile private var listener: PosLinkTransport.Listener? = null
    @Volatile private var running = false
    @Volatile private var connected = false
    private var thread: Thread? = null
    private val writeLock = Any()

    override fun start(listener: PosLinkTransport.Listener) {
        this.listener = listener
        running = true
        thread = Thread(::runLoop, "PclSerialTransport").apply {
            isDaemon = true
            start()
        }
    }

    override fun stop() {
        running = false
        listener = null
        thread?.interrupt()
        io.close()
    }

    override fun send(message: PosMessage): Boolean {
        if (!connected) return false
        return try {
            synchronized(writeLock) { io.write(PclFrameCodec.encodeMessage(message)) }
            true
        } catch (e: Exception) {
            Log.w(TAG, "send failed: ${e.message}")
            false
        }
    }

    private fun runLoop() {
        val buffer = ByteArray(4096)
        while (running) {
            try {
                if (!io.open()) {
                    Thread.sleep(RETRY_DELAY_MS)
                    continue
                }
            } catch (e: Exception) {
                Log.w(TAG, "serial open failed: ${e.message}")
                try { Thread.sleep(RETRY_DELAY_MS) } catch (_: InterruptedException) { return }
                continue
            }

            connected = true
            listener?.onConnected()
            val decoder = PclFrameCodec.Decoder()

            try {
                while (running) {
                    val n = io.read(buffer)
                    if (n < 0) break
                    if (n == 0) continue
                    decoder.feed(buffer, n) { tlvs -> handleFrame(tlvs) }
                }
            } catch (e: Exception) {
                Log.w(TAG, "serial read failed: ${e.message}")
            }

            connected = false
            io.close()
            listener?.onDisconnected()
            if (running) {
                try { Thread.sleep(RETRY_DELAY_MS) } catch (_: InterruptedException) { return }
            }
        }
    }

    private fun handleFrame(tlvs: List<PclFrameCodec.Tlv>) {
        if (sendAcks) {
            try {
                synchronized(writeLock) { io.write(byteArrayOf(PclFrameCodec.ACK)) }
            } catch (_: Exception) { /* dropped port; read loop will notice */ }
        }
        val json = tlvs.firstOrNull { it.tag == PclFrameCodec.TAG_JSON }?.value
            ?: return // not a WinkPos payload (e.g. a PxRetailer command) — ignore
        PosMessage.fromJson(String(json, Charsets.UTF_8))?.let { msg ->
            listener?.onMessage(msg)
        }
    }

    private companion object {
        const val TAG = "PclSerialTransport"
        const val RETRY_DELAY_MS = 3_000L
    }
}
