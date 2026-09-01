# winkpos — Bloomingdale's biometric check-in demo (Android)

Customer-facing Android kiosk app for the WinkPay demo. Bloomingdale's-style UI
with **face and palm check-in** via the embedded WinkPay SDK (v1.7.9), a
Loyallist dashboard, and a Pay button that charges the customer's saved card by
calling the **Wink payments API directly** from the host app.

## Flow

1. **Welcome** — "b connected" panel with Signup (QR), **Face**, and **Palm**
   tiles. Face/Palm call `WinkPayEmbedded.startCheckin(...)`
   (`biometricType = "face" | "palm"`, identity-only, no charge) and launch the
   SDK's native capture UI via `createNativeIntent`.
2. **Dashboard** — on `onCheckinSuccess`, `loginResponseJson` is parsed
   (`userPaymentDetails.user`, `.cards`) into `CheckinSession`: greeting,
   Loyallist points/rewards/offers (mock), order summary, and the customer's
   preferred saved card. **Tap the cart icon to "scan" the two demo items.**
   The Rewards REDEEM applies a $10 credit.
3. **Pay** — `WinkPaymentsClient.purchase(...)` POSTs a Merchant-Initiated
   Transaction to `POST {payments-api}/v1/payment/purchase` with
   `Authorization: Basic base64(MERCHANT_CLIENT_SECRET)` and the check-in
   card's `winkCardToken` (amount in minor units). Success → Thank-you screen
   (auto-returns to Welcome).

## Setup

- SDK is vendored at `vendor/winkpay-sdk/winkpay-sdk-repo` (unzipped from
  `winkpay-sdk-1.7.9.zip`; includes the transitive `com.palmid:palmid-core`).
- Credentials live in `local.properties` (gitignored):

  ```properties
  WINK_CLIENT_ID=...
  WINK_MERCHANT_CLIENT_SECRET=...
  WINK_ENV=stage   # qa | stage | prod
  ```

- Build & install (JDK 17 is pinned via `org.gradle.java.home` — note that path
  is macOS-specific; on Windows/Linux point it at a local JDK 17–21):

  ```sh
  ./gradlew :app:assembleDebug
  adb install -r app/build/outputs/apk/debug/app-debug.apk
  ```

## Register link over PXRRS

Per PAX's BloomingdaleDemo sequence diagram this app is the `:WinkPay`
participant: it subscribes to the PxRetailer REST service running on the *same*
terminal and takes the biometric leg of the sale. Enable it in
`local.properties`:

```properties
POS_LINK_MODE=pxrrs
# optional; defaults to https://127.0.0.1:9090
POS_LINK_PXRRS_URL=https://127.0.0.1:9090
```

PXRRS serves HTTPS and requires a client certificate even on loopback, so drop
the keystore at `app/src/main/assets/pxrrs-integration-client.p12` (password
`pax12345`). Derive it from the PAX bundle's `integrationCustomer.jks` — see
`windowsterminal/certs/README.md`. It is gitignored.

Two trigger paths are handled, so the app works before and after PAX ships the
custom form package:

| Terminal package | Trigger | Amount source |
|---|---|---|
| Stock `PxRetail` | `PAYMENTSTATUS` = `face`/`palm` fired by the form's tender button | `STR.AMOUNTOK`, mirrored there by the register |
| PAX custom | `IS_TRANS_STARTED=1` | `START_TRANS_REQ_DATA` (JSON order details) |

The register backgrounds PxRetailer (`BOOL.FOREGROUND=false`) when it sees the
biometric tender, which is what lets this app take the screen. Launching an
activity while backgrounded needs the overlay appop:

```sh
adb shell appops set com.bloomingdales.winkpos SYSTEM_ALERT_WINDOW allow
```

## Notes

- `minSdk 24`, `useLegacyPackaging = true` (palm engine native libs), ABIs
  `arm64-v8a` + `armeabi-v7a` (32-bit devices run face-only).
- CAMERA is requested at runtime on the Welcome screen — the SDK does not
  request it itself.
- An unenrolled face/palm returns `USER_NOT_ENROLLED` / "user not Found" from
  stage; enroll first (e.g. via Wink signup) to reach the dashboard.
- Debug builds export Dashboard/ThankYou activities
  (`app/src/debug/AndroidManifest.xml`) so screens can be launched from adb
  for UI checks; release builds keep them unexported.
- Tested on a Clover C505 (landscape).
