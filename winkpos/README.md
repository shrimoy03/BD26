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

## Launching into face capture from the register

Windows cannot start an Android app: intents are on-device IPC, PXRRS has no
launch command (checked against the full API list), and adb is a dev-only path.
`BOOL.FOREGROUND=false` does not *start* anything either — it only drops
PxRetailer's foreground so whatever is already running becomes visible.

So the launch has to come from this side. This app stays resident, watches the
mailbox, and fires the intent **to itself** when the register hands over a
biometric sale — which is why PAX's diagram sets `FOREGROUND=false` at startup
and assumes WinkPay is already up.

Build checklist, in order:

1. `cp local.properties.example local.properties` and fill in `WINK_CLIENT_ID`
   and `WINK_MERCHANT_CLIENT_SECRET`. `POS_LINK_MODE=pxrrs` must be set or the
   link is compiled out and nothing happens.
2. `app/src/main/assets/pxrrs-integration-client.p12` is committed — check it
   survived your clone; PXRRS needs it even over loopback.
3. `./gradlew :app:assembleDebug && adb install -r app/build/outputs/apk/debug/app-debug.apk`
4. `adb shell appops set com.bloomingdales.winkpos SYSTEM_ALERT_WINDOW allow`
   — without it `startActivity` is silently dropped while backgrounded, which
   looks exactly like the handover failing.
5. Leave the app running (it may sit behind PxRetailer). Confirm the link with
   `adb logcat -s PxrrsTransport` — you want `order received:` when the register
   sends a FACE tender.

The register side is already verified against an A3700: it publishes the order,
raises the flag and backgrounds PxRetailer. T8 at checkout (or T7 on the opening
screen) triggers a FACE tender directly for testing, without needing the
terminal's own Face button.

## Register link over PXRRS

Per PAX's BloomingdaleDemo sequence diagram this app is the `:WinkPay`
participant: it subscribes to the PxRetailer REST service running on the *same*
terminal and takes the biometric leg of the sale. Enable it in
`local.properties`:

```properties
POS_LINK_MODE=pxrrs
# optional; defaults to https://127.0.0.1:9090
POS_LINK_PXRRS_URL=https://127.0.0.1:9090

# Mailbox variables. Optional — these are the defaults, and they must match the
# register's settings.json. A PxDesigner Text variable FOO is addressed over
# REST as STR.FOO (Boolean -> BOOL.), so keep the prefix.
POS_REQUEST_VAR=STR.GENERIC_1
POS_STATE_VAR=STR.GENERIC_2
POS_RESULT_VAR=STR.TRANSACTION_RESULT
```

PXRRS serves HTTPS and requires a client certificate even on loopback, so drop
the keystore at `app/src/main/assets/pxrrs-integration-client.p12` (password
`pax12345`). Derive it from the PAX bundle's `integrationCustomer.jks` — see
`windowsterminal/certs/README.md`. It is gitignored.

Two trigger paths are handled, so the app works before and after PAX ships the
custom form package:

The register drives it: it watches for the tender button, publishes the order
into the request mailbox and raises the state flag; this app picks that up,
captures the biometric, writes the result back and raises the flag again.

| State flag | Meaning |
|---|---|
| `1` | register published the order; this app should capture |
| `3` | capture claimed and in progress |
| `2` | result published; the register settles the sale |

`FireEvent` is deliberately not relied on: PXRRS accepts a notify subscription
and then never posts to the replyURL (reproducible with `emvDetectICCard`, so
it is not specific to custom form events). The tender button therefore carries a
PxDesigner **SetVariable** action instead, which the register polls.

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
