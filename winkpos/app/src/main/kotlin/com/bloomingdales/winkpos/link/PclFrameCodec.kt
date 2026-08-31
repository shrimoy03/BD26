package com.bloomingdales.winkpos.link

import java.util.zip.CRC32

/**
 * PCL (POS Control Link) frame codec — the wire format JPxSerialServer
 * relays between the register PC and this device over USB serial.
 *
 * Framing, reverse-engineered from the worked example in
 * JPxSerialServiceUserGuide-V2.8 ("TCP Service" section) and verified against
 * its checksum:
 *
 *   STX(0x02) | u16be payloadLen | TLVs | ETX(0x03) | CRC32be
 *
 *   - payloadLen counts the TLV bytes only.
 *   - CRC-32 (standard ISO-HDLC / zlib polynomial) over STX..ETX inclusive,
 *     big-endian trailer. Verified: the guide's getPackageList frame
 *     02 001A E000 0005 "PXRRS" E001 0008 "TERMINAL" E900 0001 01 03
 *     carries trailer 92 80 79 F2 == CRC32(STX..ETX).
 *   - TLV: u16be tag | u16be length | value.
 *     Observed tags: 0xE000 source app ("PXRRS"), 0xE001 destination
 *     ("TERMINAL"), 0xE900 command id.
 *
 * TODO(PAX): confirm against the "POS Control Link (PCL) Messaging Protocol
 * Specification" before field use —
 *   1. ACK/NAK bytes and timing (the guide mentions a 3 s ACK timeout;
 *      0x06/0x15 below are the conventional values, unconfirmed).
 *   2. Whether JPxSerialServer routes/validates on the E000/E001 tags or
 *      relays any well-formed frame (we assume relay).
 *   3. A reserved tag for opaque application payloads; 0xE100 below is our
 *      placeholder for the WinkPos JSON protocol.
 */
object PclFrameCodec {

    const val STX: Byte = 0x02
    const val ETX: Byte = 0x03
    const val ACK: Byte = 0x06 // TODO(PAX): confirm
    const val NAK: Byte = 0x15 // TODO(PAX): confirm

    const val TAG_SOURCE = 0xE000
    const val TAG_DEST = 0xE001
    const val TAG_COMMAND = 0xE900
    const val TAG_JSON = 0xE100 // WinkPos payload — placeholder, see TODO above

    const val SOURCE_TERMINAL = "WINKPOS"
    const val DEST_POS = "POS"

    data class Tlv(val tag: Int, val value: ByteArray)

    fun encode(tlvs: List<Tlv>): ByteArray {
        val payloadLen = tlvs.sumOf { 4 + it.value.size }
        val frame = ByteArray(3 + payloadLen + 1 + 4)
        var i = 0
        frame[i++] = STX
        frame[i++] = (payloadLen ushr 8).toByte()
        frame[i++] = payloadLen.toByte()
        for (t in tlvs) {
            frame[i++] = (t.tag ushr 8).toByte()
            frame[i++] = t.tag.toByte()
            frame[i++] = (t.value.size ushr 8).toByte()
            frame[i++] = t.value.size.toByte()
            t.value.copyInto(frame, i)
            i += t.value.size
        }
        frame[i++] = ETX
        val crc = CRC32().apply { update(frame, 0, i) }.value
        frame[i++] = (crc ushr 24).toByte()
        frame[i++] = (crc ushr 16).toByte()
        frame[i++] = (crc ushr 8).toByte()
        frame[i] = crc.toByte()
        return frame
    }

    /** Wraps one WinkPos protocol message in a PCL frame. */
    fun encodeMessage(message: PosMessage): ByteArray = encode(
        listOf(
            Tlv(TAG_SOURCE, SOURCE_TERMINAL.toByteArray(Charsets.US_ASCII)),
            Tlv(TAG_DEST, DEST_POS.toByteArray(Charsets.US_ASCII)),
            Tlv(TAG_JSON, message.toJson().toByteArray(Charsets.UTF_8)),
        ),
    )

    fun parseTlvs(payload: ByteArray): List<Tlv> {
        val out = mutableListOf<Tlv>()
        var i = 0
        while (i + 4 <= payload.size) {
            val tag = ((payload[i].toInt() and 0xFF) shl 8) or (payload[i + 1].toInt() and 0xFF)
            val len = ((payload[i + 2].toInt() and 0xFF) shl 8) or (payload[i + 3].toInt() and 0xFF)
            i += 4
            if (i + len > payload.size) break
            out += Tlv(tag, payload.copyOfRange(i, i + len))
            i += len
        }
        return out
    }

    /**
     * Incremental frame extractor: feed raw serial bytes, get complete
     * CRC-valid TLV payloads out. Resynchronizes on the next STX after any
     * corrupt frame. Single ACK/NAK bytes between frames are surfaced via
     * [onControl].
     */
    class Decoder(private val onControl: (Byte) -> Unit = {}) {
        private val buf = ArrayDeque<Byte>()

        fun feed(bytes: ByteArray, count: Int, onFrame: (List<Tlv>) -> Unit) {
            for (i in 0 until count) buf.addLast(bytes[i])
            while (true) {
                // Skip noise / control bytes until a frame start.
                while (buf.isNotEmpty() && buf.first() != STX) {
                    val b = buf.removeFirst()
                    if (b == ACK || b == NAK) onControl(b)
                }
                if (buf.size < 3) return
                val it = buf.iterator(); it.next()
                val len = ((it.next().toInt() and 0xFF) shl 8) or (it.next().toInt() and 0xFF)
                val total = 3 + len + 1 + 4
                if (buf.size < total) return
                val frame = ByteArray(total)
                for (j in 0 until total) frame[j] = buf.elementAt(j)
                val crcExpected = CRC32().apply { update(frame, 0, 3 + len + 1) }.value
                val crcActual =
                    ((frame[total - 4].toLong() and 0xFF) shl 24) or
                        ((frame[total - 3].toLong() and 0xFF) shl 16) or
                        ((frame[total - 2].toLong() and 0xFF) shl 8) or
                        (frame[total - 1].toLong() and 0xFF)
                if (frame[3 + len] == ETX && crcExpected == crcActual) {
                    repeat(total) { buf.removeFirst() }
                    onFrame(parseTlvs(frame.copyOfRange(3, 3 + len)))
                } else {
                    buf.removeFirst() // corrupt: drop this STX, hunt for the next
                }
            }
        }
    }
}
