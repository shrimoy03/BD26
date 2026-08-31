package com.bloomingdales.winkpos.link

/**
 * A bidirectional message pipe to the merchant register. Implementations:
 *  - [WebSocketTransport]: Wi-Fi/LAN, connects to the register's embedded
 *    WebSocket server (ws://<register-ip>:8181/pos). Works today.
 *  - [PclSerialTransport]: USB, PCL frames relayed by JPxSerialServer on the
 *    register PC. Needs PAX serial-port access on the device (see SerialIo).
 *
 * Callbacks arrive on transport-internal threads; PosLink re-dispatches to
 * the main thread before the app sees them.
 */
interface PosLinkTransport {
    interface Listener {
        fun onConnected()
        fun onDisconnected()
        fun onMessage(message: PosMessage)
    }

    /** Begin connecting; keeps retrying in the background until [stop]. */
    fun start(listener: Listener)

    fun stop()

    /** Best-effort send; returns false when not connected. */
    fun send(message: PosMessage): Boolean
}
