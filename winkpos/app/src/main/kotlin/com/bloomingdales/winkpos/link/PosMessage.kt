package com.bloomingdales.winkpos.link

import org.json.JSONObject

/**
 * Wire protocol shared with the merchant register (windowsterminal —
 * Models/PosMessages.cs). One JSON object per message, camelCase keys.
 * The same payload rides on every transport (WebSocket text frame, or the
 * TLV payload of a PCL frame relayed through JPxSerialServer).
 */
data class PosMessage(
    val type: String,
    val orderId: String? = null,
    val amountCents: Long? = null,
    val currency: String? = null,
    val status: String? = null,   // APPROVED | DECLINED | CANCELLED
    val method: String? = null,   // shown as the tender label on the register
    val reason: String? = null,   // shown on decline
    val token: String? = null,    // card token for the gateway simulator
    val discountCents: Long? = null,  // coupon redeemed on the terminal, already off amountCents
    val discountLabel: String? = null,
    val checkin: Boolean? = null,     // START_PAYMENT / DISPLAY_CART in check-in mode
    val customerLabel: String? = null, // CHECKIN_READY: "Sarah · card ending 1234"
) {
    fun toJson(): String = JSONObject().apply {
        put("type", type)
        orderId?.let { put("orderId", it) }
        amountCents?.let { put("amountCents", it) }
        currency?.let { put("currency", it) }
        status?.let { put("status", it) }
        method?.let { put("method", it) }
        reason?.let { put("reason", it) }
        token?.let { put("token", it) }
        discountCents?.let { put("discountCents", it) }
        discountLabel?.let { put("discountLabel", it) }
        checkin?.let { put("checkin", it) }
        customerLabel?.let { put("customerLabel", it) }
    }.toString()

    companion object {
        // Register -> terminal
        const val TYPE_START_PAYMENT = "START_PAYMENT"
        const val TYPE_CANCEL_PAYMENT = "CANCEL_PAYMENT"
        const val TYPE_DISPLAY_CART = "DISPLAY_CART"
        /** Check-in mode: the cashier is done — charge amountCents. */
        const val TYPE_COMPLETE_PAYMENT = "COMPLETE_PAYMENT"

        // Terminal -> register
        const val TYPE_HELLO = "HELLO"
        const val TYPE_PAYMENT_RESULT = "PAYMENT_RESULT"
        /** Check-in mode: biometric passed, WinkPay is waiting for the total. */
        const val TYPE_CHECKIN_READY = "CHECKIN_READY"

        const val STATUS_APPROVED = "APPROVED"
        const val STATUS_DECLINED = "DECLINED"
        const val STATUS_CANCELLED = "CANCELLED"

        fun fromJson(json: String): PosMessage? = try {
            val o = JSONObject(json)
            val type = o.optString("type")
            if (type.isBlank()) null else PosMessage(
                type = type,
                orderId = o.optString("orderId").ifBlank { null },
                amountCents = if (o.has("amountCents")) o.optLong("amountCents") else null,
                currency = o.optString("currency").ifBlank { null },
                status = o.optString("status").ifBlank { null },
                method = o.optString("method").ifBlank { null },
                reason = o.optString("reason").ifBlank { null },
                token = o.optString("token").ifBlank { null },
                discountCents = if (o.has("discountCents")) o.optLong("discountCents") else null,
                discountLabel = o.optString("discountLabel").ifBlank { null },
                checkin = if (o.has("checkin")) o.optBoolean("checkin") else null,
                customerLabel = o.optString("customerLabel").ifBlank { null },
            )
        } catch (_: Exception) {
            null
        }
    }
}
