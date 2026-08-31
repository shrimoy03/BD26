package com.bloomingdales.winkpos.link

import android.content.Context

/**
 * Minimal serial-port abstraction so PclSerialTransport stays independent of
 * the PAX SDK. The register PC's JPxSerialServer talks to this device over
 * the USB serial port; on PAX Android terminals that port is only reachable
 * through PAX's NeptuneLite DAL — plug it in via [PaxNeptuneSerialIo].
 */
interface SerialIo {
    /** Open the port; return false to retry later. */
    fun open(): Boolean

    /** Blocking read into [buffer]; returns bytes read, 0 on timeout, -1 when the port died. */
    fun read(buffer: ByteArray): Int

    fun write(bytes: ByteArray)

    fun close()
}

/**
 * PAX NeptuneLite implementation — TO BE COMPLETED when PAX supplies
 * neptunelite.jar (their DAL client library) for this terminal model.
 *
 * Expected shape once the jar is in app/libs and added to dependencies:
 *
 *   private val dal = NeptuneLiteUser.getInstance().getDal(context)
 *   private val port = dal.commManager /* or getPort(...) — exact accessor
 *                       and port name (e.g. "USB", "COM1") vary by model;
 *                       PAX to confirm for this device */
 *   open()  -> port.open/init with the baud settings JPxSerialServer uses
 *   read()  -> port.recv(buffer, timeoutMs)
 *   write() -> port.send(bytes)
 *   close() -> port.close()
 *
 * Also ask PAX whether the app needs their signing/whitelisting for DAL
 * access on production-keyed terminals.
 */
class PaxNeptuneSerialIo(@Suppress("unused") private val context: Context) : SerialIo {
    override fun open(): Boolean {
        throw UnsupportedOperationException(
            "PaxNeptuneSerialIo needs PAX's NeptuneLite library — see the class comment.",
        )
    }

    override fun read(buffer: ByteArray): Int = -1
    override fun write(bytes: ByteArray) = Unit
    override fun close() = Unit
}
