package com.bloomingdales.winkpos

import android.util.Base64
import android.util.Log
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

/**
 * Direct client for the Wink payments API — the host-side "Pay" button charges
 * the card returned by check-in. A face check-in carries the customer's Wink
 * seed, so it pays with the customer-initiated [sale]; without one (palm
 * check-in) it falls back to a Merchant-Initiated Transaction:
 *
 *   POST {payments-api}/v1/payment/purchase
 *   Authorization: Basic base64(MERCHANT_CLIENT_SECRET)
 *   { winkCardToken, amount (minor units), currencyCode, description,
 *     applyDifferentialCharge, orderId }
 *
 * Blocking — call from a background thread.
 */
object WinkPaymentsClient {

    private const val TAG = "WinkPayments"

    class PaymentDeclinedException(message: String) : IOException(message)

    /** The gateway answered non-2xx: it refused the call, so nothing was charged. */
    open class GatewayRejectedException(message: String) : IOException(message)

    /** The gateway's "Customer consent required." 400 on the MIT purchase. */
    class ConsentRequiredException(message: String) : GatewayRejectedException(message)

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

        val request = Request.Builder()
            .url("${paymentsBaseUrl(env)}/v1/payment/purchase")
            .addHeader("Content-Type", "application/json")
            .addHeader("Authorization", basicAuth(merchantClientSecret))
            .post(body.toString().toRequestBody(JSON))
            .build()

        val json = execute(request, "purchase", orderId, amountCents)
        return PurchaseResult(
            transactionId = requireTransactionId(json),
            amountCents = json.optInt("amount", amountCents),
            currencyCode = json.optString("currencyCode", currencyCode),
        )
    }

    /**
     * Customer-initiated card sale — the same two calls the SDK's own payment
     * screen makes for face and palm (PaymentApi.createPaymentSession +
     * cardSale): a payment session for the amount, authorised with the
     * merchant secret, then the sale authorised by the customer's Wink seed
     * from the check-in scan:
     *
     *   POST {payments-api}/v1/payment/session      Basic base64(MERCHANT_CLIENT_SECRET)
     *   POST {payments-api}/V1/payment/card/sale    Basic base64(sessionId), X-Wink-Seed
     *
     * The gateway takes no order id on this path.
     */
    fun sale(
        env: String,
        merchantClientSecret: String,
        winkSeed: String,
        winkCardToken: String,
        amountCents: Int,
        currencyCode: String = "USD",
        orderId: String,
        description: String,
    ): PurchaseResult {
        require(merchantClientSecret.isNotBlank()) { "WINK_MERCHANT_CLIENT_SECRET is not configured" }
        require(winkSeed.isNotBlank()) { "No Wink seed from check-in" }
        require(winkCardToken.isNotBlank()) { "No card token from check-in" }
        require(amountCents > 0) { "Amount must be greater than zero" }

        val sessionBody = JSONObject().apply {
            put("paymentId", JSONObject.NULL)
            put("customerToken", JSONObject.NULL)
            put("integrationToken", JSONObject.NULL)
            put("amount", amountCents)
            put("currencyCode", currencyCode)
            put("returnUrl", JSONObject.NULL)
            put("cancelUrl", JSONObject.NULL)
            put("description", description)
            put("integrationType", "checkout")
            put("IntegrationMode", 1)
        }
        val sessionRequest = Request.Builder()
            .url("${paymentsBaseUrl(env)}/v1/payment/session")
            .addHeader("Content-Type", "application/json")
            .addHeader("Authorization", basicAuth(merchantClientSecret))
            .post(sessionBody.toString().toRequestBody(JSON))
            .build()
        val sessionId = execute(sessionRequest, "sale session", orderId, amountCents)
            .optString("sessionId")
            .takeIf { it.isNotBlank() && it != "null" }
            ?: throw IOException("Session ID missing from response")

        val saleRequest = Request.Builder()
            .url("${paymentsBaseUrl(env)}/V1/payment/card/sale")
            .addHeader("X-Wink-Seed", winkSeed)
            .addHeader("Content-Type", "application/json")
            .addHeader("Authorization", basicAuth(sessionId))
            .post(JSONObject().put("winkCardToken", winkCardToken).toString().toRequestBody(JSON))
            .build()
        val json = execute(saleRequest, "sale", orderId, amountCents)
        return PurchaseResult(
            transactionId = requireTransactionId(json),
            amountCents = json.optInt("amount", amountCents),
            currencyCode = json.optString("currencyCode", currencyCode),
        )
    }

    private fun basicAuth(key: String): String =
        "Basic " + Base64.encodeToString(key.toByteArray(Charsets.UTF_8), Base64.NO_WRAP)

    /**
     * Runs one gateway call. A non-2xx answer throws [GatewayRejectedException]
     * ([ConsentRequiredException] for the consent 400) — the gateway refused,
     * so nothing was charged. Network failures surface as plain IOExceptions.
     */
    private fun execute(request: Request, label: String, orderId: String, amountCents: Int): JSONObject {
        client.newCall(request).execute().use { response ->
            val text = response.body?.string().orEmpty()
            if (!response.isSuccessful) {
                // The gateway's reason is only in the body — log it so a
                // failed charge is diagnosable from logcat.
                Log.w(TAG, "$label HTTP ${response.code} order=$orderId amount=$amountCents " +
                    "body=${text.take(500)}")
                val reason = extractError(text)
                val message = if (reason != null) "$reason (HTTP ${response.code})"
                else "Payment failed (HTTP ${response.code})"
                if (reason?.contains("consent required", ignoreCase = true) == true) {
                    throw ConsentRequiredException(message)
                }
                throw GatewayRejectedException(message)
            }
            val json = JSONObject(text)
            if (json.has("isSuccess") && !json.optBoolean("isSuccess", true)) {
                Log.w(TAG, "$label declined order=$orderId amount=$amountCents body=${text.take(500)}")
                throw PaymentDeclinedException(
                    extractError(text) ?: "Payment could not be completed",
                )
            }
            return json
        }
    }

    private fun requireTransactionId(json: JSONObject): String =
        json.optString("transactionId").takeIf { it.isNotBlank() && it != "null" }
            ?: throw IOException("Transaction ID missing from response")

    /** Wink services differ in casing ("message" vs "Message"). */
    private fun extractError(bodyText: String): String? = try {
        val json = JSONObject(bodyText)
        json.optString("message").takeIf { it.isNotBlank() }
            ?: json.optString("Message").takeIf { it.isNotBlank() }
            ?: json.optJSONObject("paymentExternalResponse")
                ?.optString("winkErrorMessage")?.takeIf { it.isNotBlank() }
            ?: json.optString("error").takeIf { it.isNotBlank() }
    } catch (_: Exception) {
        null
    }
}
