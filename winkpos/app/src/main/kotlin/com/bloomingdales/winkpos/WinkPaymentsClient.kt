package com.bloomingdales.winkpos

import android.util.Base64
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

/**
 * Direct client for the Wink payments API — the host-side "Pay" button charges
 * the card returned by check-in via a Merchant-Initiated Transaction:
 *
 *   POST {payments-api}/v1/payment/purchase
 *   Authorization: Basic base64(MERCHANT_CLIENT_SECRET)
 *   { winkCardToken, amount (minor units), currencyCode, description,
 *     applyDifferentialCharge, orderId }
 *
 * Blocking — call from a background thread.
 */
object WinkPaymentsClient {

    class PaymentDeclinedException(message: String) : IOException(message)

    data class PurchaseResult(
        val transactionId: String,
        val amountCents: Int,
        val currencyCode: String,
    )

    private val JSON = "application/json; charset=utf-8".toMediaType()

    private val client = OkHttpClient.Builder()
        .connectTimeout(20, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .build()

    private fun paymentsBaseUrl(env: String): String = when (env.lowercase()) {
        "prod" -> "https://payments-api.winkapis.com"
        "qa" -> "https://qapayments-api.winkapis.com"
        else -> "https://stagepayments-api.winkapis.com"
    }

    fun purchase(
        env: String,
        merchantClientSecret: String,
        winkCardToken: String,
        amountCents: Int,
        currencyCode: String = "USD",
        orderId: String,
        description: String,
    ): PurchaseResult {
        require(merchantClientSecret.isNotBlank()) { "WINK_MERCHANT_CLIENT_SECRET is not configured" }
        require(winkCardToken.isNotBlank()) { "No card token from check-in" }
        require(amountCents > 0) { "Amount must be greater than zero" }

        val body = JSONObject().apply {
            put("winkCardToken", winkCardToken)
            put("amount", amountCents)
            put("currencyCode", currencyCode)
            put("description", description)
            put("applyDifferentialCharge", false)
            put("orderId", orderId)
        }

        val basic = "Basic " + Base64.encodeToString(
            merchantClientSecret.toByteArray(Charsets.UTF_8), Base64.NO_WRAP,
        )

        val request = Request.Builder()
            .url("${paymentsBaseUrl(env)}/v1/payment/purchase")
            .addHeader("Content-Type", "application/json")
            .addHeader("Authorization", basic)
            .post(body.toString().toRequestBody(JSON))
            .build()

        client.newCall(request).execute().use { response ->
            val text = response.body?.string().orEmpty()
            if (!response.isSuccessful) {
                throw IOException(extractError(text) ?: "Payment failed (HTTP ${response.code})")
            }
            val json = JSONObject(text)
            if (json.has("isSuccess") && !json.optBoolean("isSuccess", true)) {
                throw PaymentDeclinedException(
                    extractError(text) ?: "Payment could not be completed",
                )
            }
            val transactionId = json.optString("transactionId")
            if (transactionId.isBlank()) {
                throw IOException("Transaction ID missing from response")
            }
            return PurchaseResult(
                transactionId = transactionId,
                amountCents = json.optInt("amount", amountCents),
                currencyCode = json.optString("currencyCode", currencyCode),
            )
        }
    }

    private fun extractError(bodyText: String): String? = try {
        val json = JSONObject(bodyText)
        json.optString("message").takeIf { it.isNotBlank() }
            ?: json.optJSONObject("paymentExternalResponse")
                ?.optString("winkErrorMessage")?.takeIf { it.isNotBlank() }
            ?: json.optString("error").takeIf { it.isNotBlank() }
    } catch (_: Exception) {
        null
    }
}
