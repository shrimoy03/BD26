package com.bloomingdales.winkpos

import org.json.JSONObject

/**
 * Process-wide holder for the customer identified by the WinkPay check-in.
 * Populated in WelcomeActivity from EmbeddedCheckinResult.loginResponseJson,
 * consumed by DashboardActivity, cleared on Sign Out.
 */
object CheckinSession {

    data class Card(
        val winkCardToken: String,
        val cardNumberAlias: String,
        val nickName: String,
        val issuer: String,
        val isDefault: Boolean,
        val isExpired: Boolean,
    ) {
        /** Last 4 digits pulled out of the alias (e.g. "**** 1234" -> "1234"). */
        val last4: String
            get() = cardNumberAlias.filter { it.isDigit() }.takeLast(4).ifEmpty { "····" }
    }

    var firstName: String = ""
        private set
    var winkTag: String? = null
        private set
    var accessToken: String? = null
        private set
    var cards: List<Card> = emptyList()
        private set

    // Never falls back to an expired card — better "no card on file" than a
    // guaranteed decline.
    val preferredCard: Card?
        get() = cards.firstOrNull { it.isDefault && !it.isExpired }
            ?: cards.firstOrNull { !it.isExpired }

    val isActive: Boolean get() = firstName.isNotEmpty() || cards.isNotEmpty()

    fun populate(winkTag: String?, accessToken: String?, loginResponseJson: String) {
        this.winkTag = winkTag
        this.accessToken = accessToken

        val root = JSONObject(loginResponseJson)
        val details = root.optJSONObject("userPaymentDetails")
        firstName = details?.optJSONObject("user")
            ?.optString("firstName")
            ?.takeIf { it.isNotBlank() && it != "null" }
            ?: "there"

        val parsed = mutableListOf<Card>()
        val cardsArr = details?.optJSONArray("cards")
        if (cardsArr != null) {
            for (i in 0 until cardsArr.length()) {
                val c = cardsArr.optJSONObject(i) ?: continue
                val token = c.optString("winkCardToken")
                if (token.isBlank()) continue
                parsed += Card(
                    winkCardToken = token,
                    cardNumberAlias = c.optString("cardNumberAlias"),
                    nickName = c.optString("nickName"),
                    issuer = c.optString("issuer"),
                    isDefault = c.optBoolean("isDefault", false),
                    isExpired = c.optBoolean("isExpired", false),
                )
            }
        }
        cards = parsed
    }

    fun clear() {
        firstName = ""
        winkTag = null
        accessToken = null
        cards = emptyList()
    }
}
