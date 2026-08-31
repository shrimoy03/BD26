package com.bloomingdales.winkpos

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.io.IOException
import java.net.URLEncoder
import java.util.concurrent.TimeUnit

/**
 * Loyalty points stored in the Okta (Auth0) user profile — same mechanism as
 * the NRF Bloomingdale's Flutter demo (nrf_demo paymentConfPage.dart):
 *
 *   1. Machine-to-machine token:
 *        POST https://{OKTA_DOMAIN}/oauth/token
 *        { client_id, client_secret, audience: https://{domain}/api/v2/,
 *          grant_type: client_credentials }
 *   2. Read:  GET   /api/v2/users/{userId}  ->  user_metadata.points
 *   3. Award: PATCH /api/v2/users/{userId}  {"user_metadata":{"points": n}}
 *
 * The user id is the Wink profile's Okta id ("qcToken" on the Wink user
 * record). Blocking — call from a background thread. Disabled (isConfigured
 * false) when the OKTA_* keys are absent from local.properties.
 */
object OktaRewardsClient {

    /** Points awarded per completed purchase — mirrors the NRF demo. */
    const val POINTS_PER_PURCHASE = 500

    private val JSON = "application/json; charset=utf-8".toMediaType()

    private val client = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()

    private val domain get() = BuildConfig.OKTA_DOMAIN
    val isConfigured: Boolean
        get() = domain.isNotBlank() &&
            BuildConfig.OKTA_MTM_CLIENT_ID.isNotBlank() &&
            BuildConfig.OKTA_CLIENT_SECRET.isNotBlank()

    private fun fetchAccessToken(): String {
        val body = JSONObject().apply {
            put("client_id", BuildConfig.OKTA_MTM_CLIENT_ID)
            put("client_secret", BuildConfig.OKTA_CLIENT_SECRET)
            put("audience", "https://$domain/api/v2/")
            put("grant_type", "client_credentials")
        }
        val request = Request.Builder()
            .url("https://$domain/oauth/token")
            .addHeader("Content-Type", "application/json")
            .post(body.toString().toRequestBody(JSON))
            .build()
        client.newCall(request).execute().use { response ->
            val text = response.body?.string().orEmpty()
            if (!response.isSuccessful) {
                throw IOException("Okta token failed (HTTP ${response.code})")
            }
            return JSONObject(text).optString("access_token")
                .ifBlank { throw IOException("Okta token missing from response") }
        }
    }

    private fun userUrl(userId: String): String =
        "https://$domain/api/v2/users/${URLEncoder.encode(userId, "UTF-8")}"

    /** Current user_metadata.points, or 0 when unset. */
    fun fetchPoints(userId: String): Int {
        val token = fetchAccessToken()
        val request = Request.Builder()
            .url(userUrl(userId))
            .addHeader("Accept", "application/json")
            .addHeader("Authorization", "Bearer $token")
            .get()
            .build()
        client.newCall(request).execute().use { response ->
            val text = response.body?.string().orEmpty()
            if (!response.isSuccessful) {
                throw IOException("Okta points fetch failed (HTTP ${response.code})")
            }
            return JSONObject(text)
                .optJSONObject("user_metadata")
                ?.optInt("points", 0) ?: 0
        }
    }

    /** Write an absolute points balance back to the profile. */
    fun setPoints(userId: String, points: Int) {
        val token = fetchAccessToken()
        val body = JSONObject().put(
            "user_metadata", JSONObject().put("points", points),
        )
        val request = Request.Builder()
            .url(userUrl(userId))
            .addHeader("Content-Type", "application/json")
            .addHeader("Authorization", "Bearer $token")
            .patch(body.toString().toRequestBody(JSON))
            .build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) {
                throw IOException("Okta points update failed (HTTP ${response.code})")
            }
        }
    }
}
