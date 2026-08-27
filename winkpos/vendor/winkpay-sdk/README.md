# WinkPay SDK — Native Android Integration Guide

WinkPay is a biometric (face + palm) payment SDK for Android POS applications. This guide is for partner POS developers integrating the SDK into an Android host app.

You add a single dependency:

```
com.wink:winkpay-sdk:1.7.9
```

It is delivered as a local Maven repository (a directory tree, shipped in the SDK zip) that contains **two** artifacts: `com.wink:winkpay-sdk` and its palm-capture engine `com.palmid:palmid-core`. You only declare `winkpay-sdk` — the palm engine resolves **transitively** from the same repo (see §2). There is no second dependency line to add.

The customer-facing capture and confirmation UI is implemented as **native Android views** .

***

## Integration paths at a glance

| You want…                                                                                              | Use                                  | Section      |
| ------------------------------------------------------------------------------------------------------ | ------------------------------------ | ------------ |
| One APK that contains your POS UI and the WinkPay biometric engine in-process                          | **Native embedded SDK** (this doc)   | §1–§11 below |
| Two separate APKs on the same device (your POS + a standalone WinkPay app) talking via Android intents | **Companion app** (intent-based IPC) | TBA          |
| Your POS to drive a WinkPay device over the LAN (mDNS + HTTPS + WebSocket)                             | **Wireless local server**            | TBA          |

Most integrators want the embedded SDK. Use the wireless / companion paths only when you specifically need cross-process or cross-device delivery.

***

## Table of contents

1. [Quick start](#1-quick-start)
2. [Prerequisites & build setup](#2-prerequisites--build-setup)
3. [`AndroidManifest.xml` contributions](#3-androidmanifestxml-contributions)
4. [Public API surface](#4-public-api-surface)
5. [Credentials & device registration](#5-credentials--device-registration)
6. [Lifecycle & threading](#6-lifecycle--threading)
7. [Customer-facing display (dual-display devices)](#7-customer-facing-display-dual-display-devices)
8. [Palm capture details](#8-palm-capture-details)
9. [Error handling](#9-error-handling)
10. [Build notes & APK size](#10-build-notes--apk-size)
11. [Troubleshooting](#11-troubleshooting)

***

## 1. Quick start

The minimum integration is roughly 30 lines of Kotlin: initialize the SDK, register a callback, launch the customer-display Activity.

### 1.1 `settings.gradle.kts`

The SDK is delivered as a local Maven repository (a directory tree). Unzip the deliverable under `vendor/winkpay-sdk/winkpay-sdk-repo` in your project and declare the repo. The unzipped tree already contains both `com.wink:winkpay-sdk` **and** its transitive palm-capture engine `com.palmid:palmid-core` — pointing at this one directory resolves both:

```kotlin
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.PREFER_SETTINGS)
    repositories {
        google()
        mavenCentral()

        // WinkPay SDK (vendored local Maven repo, shipped in the SDK zip).
        maven { url = uri("${rootDir}/vendor/winkpay-sdk/winkpay-sdk-repo") }
    }
}
```

### 1.2 `app/build.gradle.kts`

```kotlin
android {
    defaultConfig {
        minSdk = 24
        ndk { abiFilters += "arm64-v8a" }   // 64-bit POS hardware; add "armeabi-v7a" too for mixed/32-bit fleets — see §8.2
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true       // bundled native libs must extract to lib/, see §2
        }
    }
}

dependencies {
    implementation("com.wink:winkpay-sdk:1.7.9")
}
```

That single line pulls in everything the SDK needs at runtime — the networking, camera, biometric, and UI components, plus the palm-capture engine (`com.palmid:palmid-core`, which carries the native palm libraries) — as transitive dependencies. You do not declare any of them yourself.

> **Mirrored / enterprise repos:** if your build proxies dependencies through an internal Nexus/Artifactory or enforces an artifact allowlist, note that the palm engine is a distinct transitive coordinate — `com.palmid:palmid-core:2.0.0` — that must also be mirrored/allowed. It's already inside the vendored repo above, so a normal local-repo integration needs nothing extra; this only matters if you re-host artifacts internally.

> **CameraX 1.4+ is required for palm capture.** The SDK depends on CameraX **1.4.1** (`camera-core`, `camera-camera2`, `camera-lifecycle`, `camera-view`), resolved transitively like everything else. The palm engine calls `ProcessCameraProvider` APIs that don't exist in CameraX 1.3.x — if your build forces CameraX below 1.4 (a direct dependency on an older version, a BOM, or a `resolutionStrategy`/dependency-constraint pin), the app builds fine but **crashes with `NoSuchFieldError` the moment palm capture starts**. Let the SDK's transitive 1.4.1 win, or depend on something newer yourself; never pin CameraX below 1.4.

### 1.3 Initialize once and start a payment

```kotlin
import android.app.ActivityOptions
import android.content.Context
import android.hardware.display.DisplayManager
import android.os.Bundle
import android.os.Build
import android.view.Display
import androidx.appcompat.app.AppCompatActivity
import com.wink.winkpay.WinkPaySdk
import com.wink.winkpay.embedded.EmbeddedPaymentCallback
import com.wink.winkpay.embedded.EmbeddedPaymentError
import com.wink.winkpay.embedded.EmbeddedPaymentRequest
import com.wink.winkpay.embedded.EmbeddedPaymentResult
import com.wink.winkpay.embedded.WinkPayEmbedded
import java.util.UUID

class PosMainActivity : AppCompatActivity() {

    private lateinit var winkPay: WinkPayEmbedded

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 1. Initialize the SDK. Pass merchant credentials and the device
        //    serial number directly — no .env file required.
        winkPay = WinkPayEmbedded.init(
            context = this,
            config = WinkPaySdk.Config(
                clientId             = "YOUR_CLIENT_ID",
                merchantClientSecret = "YOUR_MERCHANT_CLIENT_SECRET",
                deviceSerialNumber   = @Suppress("DEPRECATION") Build.SERIAL,
                sdkEnvironment       = "stage",   // "stage" or "prod"
            ),
        )

        findViewById<View>(R.id.payBtn).setOnClickListener { startFacePayment() }
    }

    private fun startFacePayment() {
        // 3. Register the result callback for this payment.
        val request = EmbeddedPaymentRequest(
            requestId = UUID.randomUUID().toString(),
            requestType = "PAY_FACE",      // PAY | PAY_FACE | PAY_PALM
            amount = 1500L,                // minor units (cents)
            currency = "USD",
            orderId = "ORDER-${System.currentTimeMillis()}",
            tenderId = "TENDER-001",
            cameraIndex = 0,               // 0 = front (customer-facing, default), 1 = back
            rotationAngle = 0,
        )

        winkPay.startPayment(request, object : EmbeddedPaymentCallback {
            override fun onSuccess(result: EmbeddedPaymentResult) {
                // Payment processed by the SDK (returnExternalTokens=false default):
                //   result.transactionId, result.amount, result.winkTag
                // External-token mode (returnExternalTokens=true):
                //   result.winkCardToken, result.winkTag, result.amount
            }
            override fun onCancelled(requestId: String) { /* user cancelled */ }
            override fun onFailure(error: EmbeddedPaymentError) {
                // error.errorCode is "AUTH_FAILED" or "LIVENESS_FAILED" today.
                // See §9 for the full list.
            }
        })

        // 4. Launch the customer display. On single-display devices pass
        //    Display.DEFAULT_DISPLAY (or omit) and you are done.
        val displayId = pickCustomerDisplayId()
        val intent = winkPay.createNativeIntent(this, displayId)
        val opts = ActivityOptions.makeBasic().setLaunchDisplayId(displayId)
        startActivity(intent, opts.toBundle())
    }

    private fun pickCustomerDisplayId(): Int {
        val dm = getSystemService(Context.DISPLAY_SERVICE) as DisplayManager
        return dm.displays.firstOrNull {
            it.displayId != Display.DEFAULT_DISPLAY && it.isValid
        }?.displayId ?: Display.DEFAULT_DISPLAY
    }
}
```

That's the whole integration. Everything below is reference material.

***

## 2. Prerequisites & build setup

| Requirement                | Value                    | Notes                                                                                 |
| -------------------------- | ------------------------ | ------------------------------------------------------------------------------------- |
| Android Gradle Plugin      | 8.5+ (tested with 8.5.2) | AAR metadata format                                                                   |
| Gradle                     | 8.7+                     | required by AGP 8.5                                                                   |
| Kotlin                     | 2.0+                     | SDK is compiled with 2.1.0                                                            |
| `minSdk`                   | **24**                   | SDK runtime minimum                                                                   |
| `compileSdk` / `targetSdk` | 34+                      | AndroidX compatibility                                                                |
| Java toolchain             | 11+                      | —                                                                                     |
| CameraX                    | **1.4+** (SDK ships 1.4.1) | Resolved transitively — nothing to declare. Do **not** pin CameraX below 1.4: palm capture crashes at start (see the callout in §1.2) |
| Device ABI                 | **arm64-v8a** (palm) / **armeabi-v7a** (face-only) | One distribution for all hardware. The palm engine is arm64-v8a only; 32-bit devices install fine and run face-only (palm gates off — §8.2). x86_64 emulators are not supported |

> **`useLegacyPackaging = true` is mandatory.** Without it, the palm engine's native libraries stay inside `base.apk` instead of being extracted to `lib/<abi>/`, and the SDK fails to initialize at runtime. AGP 8 makes legacy packaging opt-in.

***

## 3. `AndroidManifest.xml` contributions

The SDK manifest declares the permissions and Activities the customer display needs; manifest merger pulls them into your app automatically. You do **not** need to copy these into your own manifest.

Inherited from the SDK:

| Item                                                       | Why                                                                                                 |
| ---------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| `android.permission.CAMERA`                                | Face + palm capture                                                                                 |
| `android.permission.INTERNET`                              | Auth + payment APIs                                                                                 |
| `android.permission.RECORD_AUDIO`, `MODIFY_AUDIO_SETTINGS` | Declared by the camera subsystem; the SDK does not record audio                                     |
| `android.permission.FOREGROUND_SERVICE`                    | Reserved for the wireless server flow                                                               |
| `<uses-feature android:name="android.hardware.camera"/>`   | Play Store filter                                                                                   |
| The SDK's customer-display Activity                        | Launched by `createNativeIntent`                                                                    |

You only add what you need for your own POS UI — typically just your launcher Activity and any platform-specific intent-filter your processor or POS shell requires.

> **CAMERA is required at runtime, but the SDK does not request it for you.** You must request `android.permission.CAMERA` from the user yourself before launching a payment — typically in your app's first-launch flow or right before calling `startPayment`. If the permission is not granted when the customer-display Activity starts, the camera preview shows black and the biometric session eventually fails (there is no "permission denied" callback — request the permission proactively).

***

## 4. Public API surface

These are the only types third-party integrators are expected to touch. Everything else under `com.wink.winkpay.*` is internal.

### 4.1 `WinkPaySdk` and `WinkPaySdk.Config`

`com.wink.winkpay.WinkPaySdk` is a singleton. You don't normally call it directly — `WinkPayEmbedded.init(...)` calls it for you. Configuration is the data class `WinkPaySdk.Config`:

```kotlin
WinkPaySdk.Config(
    clientId:             String? = null,
    merchantClientSecret: String? = null,
    deviceSerialNumber:   String? = null,
    sdkEnvironment:       String  = "stage",  // "qa" | "stage" | "prod"
    palmMfaEnabled:       Boolean = true,
    palmLivenessMode:     String  = "hires",  // "hires" | "gesture" | "both"
    palmLivenessEnabled:  Boolean = true,      // master liveness switch — see below
    enableIR:             Boolean = false,     // RGB+IR palm camera module — see below
    palmEngineDriver:     String  = "session", // "session" | "bridge"
    isFrontCamera:        Boolean = true,      // camera faces the customer
    palmTrackingOverlay:  Boolean = false,     // live tracked-palm guidance box
    palmFocusMaskOpacity: Float?  = null,      // fixed focus-mask darkness (null = adaptive)
    enableHttpsServer:    Boolean = false,
    serverPort:           Int     = 8443,
    readWinkConfig:       Boolean = false,    // on-device test override; see below
    debugLogging:         Boolean = false,    // verbose SDK logs (off in production)
    envFileContent:       String? = null,     // legacy fallback
)
```

| Flag                   | Default   | Effect                                                                                                                                                                                                                           |
| ---------------------- | --------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `clientId`             | `null`    | Merchant OAuth `CLIENT_ID` issued by WinkPay. **Required** — pass it directly here (preferred) or via `envFileContent`. When both are present, the direct field wins.                                                            |
| `merchantClientSecret` | `null`    | OAuth client secret. **Required.** Same precedence rules as `clientId`.                                                                                                                                                          |
| `deviceSerialNumber`   | `null`    | POS device serial number. **Required for biometric flows.** On first run the SDK registers this serial with the WinkPay platform, persists the returned device identifier locally, and sends it on every subsequent biometric login. Safe to pass on every init — once a device identifier is cached locally, subsequent values are ignored. See §5. |
| `sdkEnvironment`       | `"stage"` | `"qa"`, `"stage"`, or `"prod"`. Selects the backend endpoints the SDK talks to. Any other value throws.                                                                                                                          |
| `palmMfaEnabled`       | `true`    | When `true` (default), the **Use palm** option appears on the MFA screen wherever the device hardware supports palm capture. Set `false` to hide palm as a fallback entirely (e.g. kiosks that ship face + PIN only). May also be toggled at runtime via the `WinkPaySdk.palmMfaEnabled` property. |
| `palmLivenessMode`     | `"hires"` | Palm anti-spoofing mode. `"hires"` (default): after capture the engine analyzes a high-resolution still under the SDK's screen fill light — no gesture prompt, fastest customer UX. `"gesture"`: the engine prompts a random hand gesture and verifies it. `"both"`: gesture gate followed by a hires gate — strongest posture, slowest UX. Devices whose camera can't run the extra still-capture stream automatically fall back from hires to gesture. Also settable at runtime via `WinkPaySdk.palmLivenessMode`. |
| `palmLivenessEnabled`  | `true`    | Master switch for the palm liveness check. Set `false` **only** on hardware whose presentation-attack defense lives below the SDK (e.g. RGB+IR camera modules — a spoof would have to replicate both the RGB print and the IR/vein image). When `false` the engine captures with no gesture prompt or hires still (`palmLivenessMode` is ignored) and the SDK's client-side liveness gates are bypassed. **On a plain RGB camera this removes the only anti-spoof layer — a photo of a palm will model. Never ship `false` to RGB-only fleets.** For actual IR hardware, prefer `enableIR = true` (below), which also fixes the backend match mode. |
| `enableIR`             | `false`   | Declare the palm-capture camera an RGB+IR module (vendor-supported IR hardware). When `true`, the engine captures with liveness off — the IR/vein channel is the presentation-attack defense on this hardware — **and** palm identification is requested in `rgb-ir` match mode, so the backend matches on both the palm print and the vein image. Do **not** set on plain RGB cameras: it disables every liveness gate with no IR channel to compensate, and the identify match mode would not correspond to the captured model. |
| `palmEngineDriver`     | `"session"` | Palm capture engine driver. `"session"` (default since 1.7.2) drives palm capture through the palm engine's own camera session — markedly better gesture-liveness reliability in low light. `"bridge"` selects the previous frame-fed pipeline, in which the SDK owns the camera and exposure management — kept as a rollback knob if the default misbehaves on your hardware. Also settable at runtime via `WinkPaySdk.palmEngineDriver`. |
| `isFrontCamera`        | `true`    | Physical geometry of the palm-capture camera relative to the customer. `true` (default): the camera **faces** the customer — selfie geometry. This is every POS customer display, *including* devices that report their customer-facing camera as `LENS_FACING_BACK` (Clover C505, Elo Pay 7), which is why this is declared by the host rather than derived from the bound lens. `false`: a true rear camera pointing **away** from the customer (the hand hovers behind a handheld device) — the SDK then stops mirroring the left/right "Move hand …" guidance (and its direction arrows) and the gesture-prompt hand artwork, which would otherwise be backwards in that geometry. Also settable at runtime via `WinkPaySdk.isFrontCamera`. |
| `palmTrackingOverlay`  | `false`   | Live tracked-palm guidance overlay on the palm capture screen: a yellow square follows the customer's palm (sized to it) with a center arrow pointing at the guide frame; the frame renders white, and both turn green on alignment. Default off — the overlay's palm-position registration doesn't hold on some small devices. When off, the screen keeps the classic guidance (gold frame turning green, instruction text, edge direction hints). |
| `palmFocusMaskOpacity` | `null`    | Darkness of the focus mask outside the palm guide frame, `0.0`–`1.0`. `null` (default): adaptive — 0.85, dropping to 0.55 when the low-light fill light activates (there the screen's glow is helping the camera see the palm, and the mask would hold it back). `0f`: no darkening at all — for devices permanently installed in dim venues, where every bit of display glow helps. Any other value: fixed opacity; the adaptive lightening is disabled. |
| `enableHttpsServer`    | `false`   | Starts the local HTTPS server on `serverPort` for the wireless POS integration path. Off by default — enabling it costs ~30–50 MB of RAM.                                                                                        |
| `serverPort`           | `8443`    | TCP port for the wireless server.                                                                                                                                                                                                |
| `readWinkConfig`       | `false`   | When `true`, the SDK reads an on-device config file at init and lets it override the camera index and rotation values you pass in the request. Intended for **on-device testing only** — leave `false` in production. Missing or malformed file falls back silently to the request values. |
| `debugLogging`         | `false`   | When `true`, the SDK emits verbose debug/info logs to logcat. **Off by default** so production builds stay quiet and don't surface operational detail. Warnings and errors are always logged regardless. You can also enable logs for a single tag at runtime without rebuilding: `adb shell setprop log.tag.<TAG> DEBUG` (e.g. `PalmIdSessionCapture`). |
| `envFileContent`       | `null`    | Legacy path: raw `.env`-formatted content (see §5). Use the direct `clientId` / `merchantClientSecret` fields above for new integrations — `envFileContent` is supported only for backward compatibility.                        |

### 4.2 `WinkPayEmbedded`

`com.wink.winkpay.embedded.WinkPayEmbedded` is the integration facade.

```kotlin
object Companion {
    fun init(
        context: Context,
        config: WinkPaySdk.Config = WinkPaySdk.Config(),
    ): WinkPayEmbedded

    fun getInstance(): WinkPayEmbedded   // throws if init() not called
}

fun startPayment(request: EmbeddedPaymentRequest, callback: EmbeddedPaymentCallback)

fun startCheckin(request: EmbeddedCheckinRequest, callback: EmbeddedPaymentCallback)

fun startLiteRegistration(request: EmbeddedLiteRegistrationRequest, callback: EmbeddedPaymentCallback)

fun createNativeIntent(context: Context, displayId: Int = -1): Intent
fun createCaptureFragment(): Fragment   // embed in your own Activity — see §4.12

fun setAgeRestrictionOverride()         // merchant-side ID override — see §4.13
fun cancelPayment()
fun shutdown()
```

| Method                               | When to call                                                                                        | Effect                                                                                                                                            |                                                                                                                                                         |
| :----------------------------------- | :-------------------------------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------ | :------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `init(context, config)`              | Once, early — typically `Activity.onCreate` or `Application.onCreate`. Subsequent calls are no-ops. | Initializes the native API layer (parses env, picks stage/prod URLs), wires up the optional wireless server, prepares for `startPayment`.         |                                                                                                                                                         |
| `startPayment(request, callback)`    | Once per payment, before launching the Activity.                                                    | Stores order data and registers your callback. Both must be called from the same process that will host the customer-display Activity.            |                                                                                                                                                         |
| `startCheckin(request, callback)`    | Identity-only flow (no payment).                                                                    | Same as `startPayment` but skips card selection — returns user identity + saved cards. See §4.6.                                                  |                                                                                                                                                         |
| `createNativeIntent(ctx, displayId)` | Right after `startPayment`.                                                                         | Returns an Intent for the SDK's customer-display Activity. Pass `displayId >= 0` for a secondary display; the method adds `FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_MULTIPLE_TASK`automatically. You still need to pass`ActivityOptions.makeBasic().setLaunchDisplayId(displayId)`to`startActivity` — see §7. |
| `setAgeRestrictionOverride()`        | When the merchant has visually verified the customer's ID for an age-restricted order.              | Clears the customer-side ID gate so payment can proceed without profile verification. See §4.13. Safe to call from any thread.                     |                                                                                                                                                         |
| `cancelPayment()`                    | If you need to abort programmatically (e.g. POS-side timeout).                                      | Triggers `onCancelled` on the active callback.                                                                                                    |                                                                                                                                                         |
| `shutdown()`                         | When you're done with WinkPay (e.g. POS app exiting).                                               | Unregisters callbacks, resets state, stops the wireless server, releases internal singletons.                                                     |                                                                                                                                                         |

### 4.3 `EmbeddedPaymentRequest`

```kotlin
data class EmbeddedPaymentRequest(
    val requestId: String,                        // your unique id; UUIDv4 recommended
    val requestType: String,                      // "PAY" | "PAY_FACE" | "PAY_PALM"
    val amount: Long,                             // minor units (cents). Must be > 0
    val currency: String = "USD",                 // ISO 4217
    val orderId: String,                          // your POS order id; non-blank
    val tenderId: String,                         // your tender id (free-form, non-blank)
    val ageRestrictedPurchase: Boolean = false,   // gates capture behind merchant override
    val rotationAngle: Int = 0,                   // 0/90/180/270 — pose hint for face detection
    val cameraIndex: Int = 0,                     // 0 = front (customer-facing, default), 1 = back
    val returnExternalTokens: Boolean = false,    // see §4.7
    val returnOnFailure: Boolean = false,         // see §4.8
    val maxRetries: Int = 3,                      // max face-capture attempts; see §4.9
    // — Tuning knobs (see §4.9) —
    val minFaceRatio: Double? = null,             // 0.0–1.0; null = device-class default
    val maxFaceRatio: Double? = null,             // 0.0–1.0; null = SDK default (0.50 landscape, 0.78 otherwise)
    val faceRatio: Double? = null,                // guide-circle target; null = class default (landscape 0.30) or derived
    val livenessEnabled: Boolean? = null,         // null = SDK default (ON unless local liveness ran)
    val previewRotation: Int? = null,             // 0/90/180/270; null = camera default
    val activityOrientation: Int? = null,         // ActivityInfo.SCREEN_ORIENTATION_* constant
    // — Palm-specific overrides (see §4.10) —
    val palmRotationAngle: Int = 0,               // -1 = fall back to rotationAngle
    val palmCameraIndex: Int = -1,                // -1 = fall back to cameraIndex
    val palmPreviewRotation: Int? = null,         // null = fall back to previewRotation
)
```

`PAY` shows the face/palm chooser; `PAY_FACE` and `PAY_PALM` jump straight into the corresponding capture screen.

### 4.4 `EmbeddedPaymentResult`

```kotlin
data class EmbeddedPaymentResult(
    val requestId: String,
    val transactionId: String?,         // null in external-token mode
    val amount: Long,
    val winkTag: String? = null,        // biometric-resolved customer id
    val winkToken: String? = null,      // payment token, when available
    val winkCardToken: String? = null,  // selected card token, populated in external-token mode
    val mfaUsed: Boolean = false,       // true if MFA was performed during this auth
    val appID: String? = null,          // processor app id — populated for processors that require it, see §4.7
)
```

### 4.5 `EmbeddedPaymentError` and `EmbeddedPaymentCallback`

```kotlin
data class EmbeddedPaymentError(
    val requestId: String,
    val errorCode: String,
    val errorMessage: String,
)

interface EmbeddedPaymentCallback {
    fun onSuccess(result: EmbeddedPaymentResult)
    fun onCancelled(requestId: String)
    fun onFailure(error: EmbeddedPaymentError)
    fun onCheckinSuccess(result: EmbeddedCheckinResult) { /* default delegates to onSuccess */ }
    fun onLiteRegistrationSuccess(result: EmbeddedLiteRegistrationResult) { /* default delegates to onSuccess */ }
}
```

All callback methods are dispatched on the main thread.

### 4.6 Check-in mode (identity-only, no payment)

Use this for kiosk check-in and similar flows where you want to identify a returning user but not charge a card. The SDK runs the biometric capture and returns the full login response.

```kotlin
data class EmbeddedCheckinRequest(
    val requestId: String,
    val currency: String = "USD",
    val rotationAngle: Int = 0,
    val cameraIndex: Int = 0,
    val biometricType: String = "face",           // "face" or "palm"
    val returnOnFailure: Boolean = false,          // see §4.8
    val maxRetries: Int = 3,                       // max face-capture attempts; see §4.9
    // — Tuning knobs (see §4.9) —
    val minFaceRatio: Double? = null,
    val maxFaceRatio: Double? = null,
    val faceRatio: Double? = null,
    val livenessEnabled: Boolean? = null,
    val previewRotation: Int? = null,
    val activityOrientation: Int? = null,
    // — Palm-specific overrides (see §4.10) —
    val palmRotationAngle: Int = 0,
    val palmCameraIndex: Int = -1,
    val palmPreviewRotation: Int? = null,
)

data class EmbeddedCheckinResult(
    val requestId: String,
    val winkTag: String?,
    val accessToken: String?,
    val loginResponseJson: String,        // full server JSON: user, cards, addresses, faceResponse
)

winkPay.startCheckin(EmbeddedCheckinRequest(requestId = UUID.randomUUID().toString()),
    object : EmbeddedPaymentCallback {
        override fun onCheckinSuccess(result: EmbeddedCheckinResult) {
            // Parse result.loginResponseJson — userPaymentDetails.user, .cards, .shippingAddresses
        }
        override fun onSuccess(r: EmbeddedPaymentResult) {}
        override fun onCancelled(requestId: String) {}
        override fun onFailure(error: EmbeddedPaymentError) {}
    })
```

**`isProfileVerified` is emitted at two locations** inside `loginResponseJson` for host integrator convenience:

* `userPaymentDetails.isProfileVerified` — the canonical field on the outer `UserPaymentDetails`.
* `userPaymentDetails.user.isProfileVerified` — mirrored onto the inner `UserShort` so hosts that read profile fields from the `user` object don't have to reach back up.

Both fields carry the same boolean and are written from the same source on the server response. Read whichever is convenient — they are kept in sync.

### 4.7 `returnExternalTokens` — let the host process the charge

If your processor already handles the actual card charge and you only need WinkPay to identify the customer and pick a card:

| `returnExternalTokens` | UI label    | SDK behaviour                                                                              | Result fields                        |
| ---------------------- | ----------- | ------------------------------------------------------------------------------------------ | ------------------------------------ |
| `false` (default)      | **Pay Now** | SDK charges the selected card via WinkPay's payment rails.                                 | `transactionId`, `amount`, `winkTag` |
| `true`                 | **Confirm** | SDK does **not** charge the card. Returns the selected card's `winkCardToken` + `winkTag`. | `winkCardToken`, `winkTag`, `amount`, `appID` (when applicable) |

When `returnExternalTokens = true` and the configured `clientId` is for a processor that requires an app id to route the charge, the SDK additionally populates `result.appID` with that value. For processors that do not need it, `appID` is `null`.

### 4.8 `returnOnFailure` — surface auth failures to the host

By default, when biometric authentication fails (face not recognized, liveness rejected, etc.) the native UI shows a **Retry / See Options** error screen. If you want control to return to your POS app instead:

```kotlin
EmbeddedPaymentRequest(..., returnOnFailure = true)
```

The native Activity finishes with `onFailure(EmbeddedPaymentError(...))` on the registered callback (see §9 for the error codes).

### 4.9 Capture tuning knobs

These are all **optional overrides** on `EmbeddedPaymentRequest` / `EmbeddedCheckinRequest` / `EmbeddedLiteRegistrationRequest`. Leave them off and the SDK picks per-device defaults — same as before any of these existed.

| Field                            | Type           | Default                | What it does |
| -------------------------------- | -------------- | ---------------------- | ------------ |
| `maxRetries`                     | `Int`          | `3`                    | Maximum number of face-capture attempts before the SDK surfaces a terminal failure. Counts the initial capture plus each "Scan again" retry, so `3` allows up to three face captures total. |
| `minFaceRatio`                   | `Double?`      | `null` (device class)  | Minimum fraction of the **camera frame** (0.0–1.0) that the detected face bounding box must span on both width *and* height. Defaults: landscape 0.20 / tablet 0.50 / phone 0.40. Lower → customer can stand further away. The landscape default already accommodates counter distance; raise it per install if recognition match rates need a closer face. |
| `maxFaceRatio`                   | `Double?`      | `null` (SDK: `0.50` landscape, `0.78` otherwise) | Maximum fraction of the **camera frame** (0.0–1.0) the face bounding box may span before the customer is asked to *"Move further away"*. Together with `minFaceRatio` this defines the accepted distance window. Raise it (e.g. `0.85`–`0.90`) on handhelds where customers naturally hold the device close. Must be greater than the effective min ratio — values that leave no window are ignored in favor of the default. Note the default cap doubles as an anti-spoof guard (a phone screen held up to the camera fills the frame), so keep it as tight as the install allows. |
| `faceRatio`                      | `Double?`      | `null` (derived)       | **UI-only.** Explicit face size (0.0–1.0 of the frame) the on-screen guide circle is sized to. It steers where inside the `minFaceRatio`–`maxFaceRatio` window the customer is asked to aim — it never changes what validation accepts (the leeway comes from min/max). Must sit strictly between the effective min and max; out-of-window values are ignored. `null` uses the landscape class default (`0.30`) on landscape devices, otherwise the derived target (min ratio + ~12% headroom). Useful with a wide window, e.g. `min 0.30, max 0.90, faceRatio 0.55`, so natural drift in either direction stays inside the window. Honored best-effort — the circle is still clamped by the containment circle, the screen, and label fit. |
| `livenessEnabled`                | `Boolean?`     | `null` (SDK default)   | Forces the server-side liveness check on or off. `null` keeps each flow's default — ON for payment/check-in (unless an on-device liveness check already ran), OFF for lite enrollment. Set `false` on supervised kiosks where local checks are sufficient. |
| `previewRotation`                | `Int?`         | `null` (camera auto)   | View-level rotation applied to the camera **preview only** (visual feed). 0/90/180/270 degrees. Use this when the device's camera sensor mounting doesn't match the display's natural orientation (some POS handhelds ship the front camera at a 90°/270° offset from the display, so the preview comes out sideways without an override). When set, the uploaded image is rotated to match — the server sees the same orientation the customer saw. |
| `activityOrientation`            | `Int?`         | `null`                 | Force the customer-facing Activity into a specific screen orientation via `setRequestedOrientation(...)`. Accepts any Android `ActivityInfo.SCREEN_ORIENTATION_*` constant. Useful on handhelds where the camera sensor only produces a server-acceptable image in one orientation. |

**Examples:**
```kotlin
// Handheld with sensor mounted at 90° pose, display rotated 270° from sensor
EmbeddedPaymentRequest(
    ...,
    rotationAngle = 90,
    previewRotation = 270,
    activityOrientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE,
)

// Kiosk: customer ~50 cm from screen — relax the strict default
EmbeddedCheckinRequest(..., minFaceRatio = 0.30)

// Handheld held close to the face — widen the distance window upward
EmbeddedPaymentRequest(..., maxFaceRatio = 0.88)

// Wide accept window with the visual target in the middle of it
EmbeddedPaymentRequest(..., minFaceRatio = 0.30, maxFaceRatio = 0.90, faceRatio = 0.55)

// Supervised registration: skip server liveness, rely on local checks
EmbeddedLiteRegistrationRequest(..., livenessEnabled = false)
```

### 4.10 Per-modality (face vs palm) capture overrides

When face and palm capture need **different** rotation/camera configs on the same device — common on hardware with a dedicated palm-side sensor, or when MFA's "Use palm" fallback expects a different grip than face capture — set these palm-specific fields. All three default to "fall back to face values," so existing integrations keep working untouched.

| Field                  | Type     | Default       | Fallback                |
| ---------------------- | -------- | ------------- | ----------------------- |
| `palmRotationAngle`    | `Int`    | `0`           | pass `-1` → `rotationAngle` |
| `palmCameraIndex`      | `Int`    | `-1`          | `cameraIndex`           |
| `palmPreviewRotation`  | `Int?`   | `null`        | `previewRotation`       |

Always send both sets when they differ so the **MFA palm fallback** (when face misses and the customer taps *Use palm* on the MFA screen) picks up the right config without the host having to re-issue a fresh request.

```kotlin
EmbeddedPaymentRequest(
    ...,
    rotationAngle = 90,  cameraIndex = 0,  previewRotation = 270,         // face
    palmRotationAngle = 0,  palmCameraIndex = 1,  palmPreviewRotation = 0, // palm
)
```

### 4.11 Lite registration

Face-enrollment + minimal profile creation (first name + phone — `lastName` and `email` are auto-generated placeholders the user updates later). Used for low-friction onboarding flows where the customer doesn't yet have a Wink account.

```kotlin
data class EmbeddedLiteRegistrationRequest(
    val requestId: String,
    val rotationAngle: Int = 0,
    val cameraIndex: Int = 0,
    val minFaceRatio: Double? = null,       // see §4.9
    val maxFaceRatio: Double? = null,       // see §4.9
    val faceRatio: Double? = null,          // see §4.9
    val livenessEnabled: Boolean? = null,   // null = OFF for enrollment by default
    val previewRotation: Int? = null,
    val activityOrientation: Int? = null,
    val maxRetries: Int = 3,                // see §4.9
)

data class EmbeddedLiteRegistrationResult(
    val requestId: String,
    val accessToken: String?,        // identity-provider access token (or platform fallback)
    val winkSeed: String?,
    val oAuthRequestId: String?,
    val firstName: String?,
    val lastName: String?,           // auto-generated placeholder
    val email: String?,              // auto-generated placeholder
    val contactNo: String?,
    val alreadyEnrolled: Boolean,    // true when the face was recognized; no form shown
    val refreshToken: String? = null,
    val idToken: String? = null,
    val expiresIn: Int? = null,
    val tokenType: String? = null,
    val isKeycloakToken: Boolean = false,  // false when the SDK fell back to the platform token
)

winkPay.startLiteRegistration(
    EmbeddedLiteRegistrationRequest(requestId = UUID.randomUUID().toString()),
    object : EmbeddedPaymentCallback {
        override fun onLiteRegistrationSuccess(result: EmbeddedLiteRegistrationResult) {
            // result.accessToken is the identity-provider token (when isKeycloakToken == true)
            // — use it as a Bearer token for downstream API calls.
        }
        override fun onSuccess(r: EmbeddedPaymentResult) {}
        override fun onCancelled(requestId: String) {}
        override fun onFailure(error: EmbeddedPaymentError) {}
    })
```

Flow:
1. Fresh face enrollment scan.
2. If the face is already known → returns `alreadyEnrolled = true` with the existing user's tokens. No form shown.
3. Otherwise → the SDK shows a compact form (first name + phone with a country picker). On submit it validates the phone number, creates the profile, then exchanges the result for an identity-provider access token. If that exchange fails it falls back to the platform token (`isKeycloakToken = false`).

### 4.12 Embedding the capture UI in your own Activity (`WinkPayCaptureFragment`)

`createNativeIntent(...)` launches the SDK's full-screen customer-display Activity. If you'd rather have the capture screen draw **inside your own layout** — next to your branded chrome, in a sidebar, in a tablet master/detail pane — call `createCaptureFragment()` instead and mount the returned `Fragment` yourself.

```kotlin
// 1. Register the result callback as usual.
winkPay.startCheckin(
    EmbeddedCheckinRequest(
        requestId = UUID.randomUUID().toString(),
        cameraIndex = 0,
        rotationAngle = 0,
    ),
    callback,
)

// 2. Mount the fragment in your container instead of launching the Activity.
supportFragmentManager.beginTransaction()
    .replace(R.id.host_capture_container, winkPay.createCaptureFragment())
    .commit()
```

The fragment reads its capture parameters from the same process-scoped state that `startCheckin` / `startPayment` populates, so always call one of those *before* mounting the fragment.

**Supported flows:** the check-in flow (face capture → API auth → `onCheckinSuccess`) works end-to-end inside the fragment today. Full payment flows (`startPayment`) are not yet exposed through this entry point because they involve multiple downstream screens (PaymentOptions, PaymentConfirmation, MFA/Consent dialogs) that aren't suited to an embedded view — keep using `createNativeIntent(...)` for those.

**Cancellation:** removing the fragment from its container at any time tears down the capture session. If you want the registered callback to receive `onCancelled`, call `winkPay.cancelPayment()` as well — by default a host-driven removal silently disposes of state.

### 4.13 Age-restricted override (`setAgeRestrictionOverride`)

When an order is age-restricted (`EmbeddedPaymentRequest.ageRestrictedPurchase = true`) and the matched customer's profile is not verified, the customer display blocks payment and shows an ID prompt. If the merchant visually checks the customer's ID and approves the sale, call:

```kotlin
winkPay.setAgeRestrictionOverride()
```

This clears the customer-side ID gate for the current order so payment can proceed without profile verification. Typically wired to an "Override" button on the merchant display. Safe to call from any thread.

***

## 5. Credentials & device registration

### 5.1 Merchant credentials

Pass the merchant credentials directly on `WinkPaySdk.Config`:

```kotlin
WinkPaySdk.Config(
    clientId             = "YOUR_CLIENT_ID",
    merchantClientSecret = "YOUR_MERCHANT_CLIENT_SECRET",
    deviceSerialNumber   = Build.SERIAL,
    sdkEnvironment       = "stage",
)
```

Both fields are required for any biometric flow. Treat the secret as exactly that — keep it out of source control, and out of strings.xml. Stage vs prod URLs are baked into the SDK and selected via `Config.sdkEnvironment` — there's nothing else to configure for a standard deployment.

### 5.2 Device registration (`deviceSerialNumber`)

The SDK requires the device to be registered with the WinkPay platform before it can run face or palm. Pass the POS serial number on every init — the SDK takes care of the rest:

1. **First launch:** a background thread registers the serial with the WinkPay platform. The platform returns a server-issued device identifier, which the SDK persists locally.
2. **Subsequent launches:** the cached device identifier is reused. The serial you pass is ignored unless the cache was cleared (e.g. app data wiped).
3. **At biometric capture time:** the SDK attaches the cached device identifier to every biometric login call.

If registration hasn't completed yet (or failed), the capture screens display a small red `!` "Device not registered" badge in the top-right corner. Biometric attempts in that state surface a "Device not registered. Please contact support." failure to your callback.

> **What to pass as `deviceSerialNumber`.** On POS hardware with API 24–25, `Build.SERIAL` typically works. On newer devices, fall back to a stable per-device synthetic ID (e.g. `"$MANUFACTURER-$MODEL-$ID"` — or your own provisioned ID). Whatever you pass on the first run is what gets registered with WinkPay; it is not mutated locally afterwards.

### 5.3 Legacy `envFileContent` path

If you have an existing integration that reads merchant credentials from a `.env`-style file, you can keep using `Config.envFileContent` — the SDK still parses `KEY=VALUE` lines for `CLIENT_ID`, `MERCHANT_CLIENT_SECRET`, and the optional URL-override keys below. **New integrations should use the direct fields in §5.1.** When both are present, the direct fields win.

| Key                      | Required | Used by     | Notes                                                                        |
| ------------------------ | -------- | ----------- | ---------------------------------------------------------------------------- |
| `CLIENT_ID`              | ✓        | Auth API    | OAuth client id issued by WinkPay                                            |
| `MERCHANT_CLIENT_SECRET` | ✓        | Auth API    | OAuth client secret                                                          |
| `BASE_LOGIN_URL`         | optional | Auth API    | Override the baked-in stage/prod URL — only set for non-standard deployments |
| `BASE_PAYMENT_URL`       | optional | Payment API | Override of baked-in URL                                                     |
| `BASE_API_URL_PALM`      | optional | Palm API    | Override of baked-in URL                                                     |
| `PMS_PROJECT_ID`         | optional | Palm API    | Override of baked-in project id                                              |

***

## 6. Lifecycle & threading

### What happens when you call `startPayment` and launch the Activity

1. `startPayment` stores the order data in process-scoped state and registers your callback.
2. `createNativeIntent` returns an Intent for the SDK's customer-display Activity.
3. The Activity reads the order data in `onCreate`. If `requestType` was `PAY_FACE` or `PAY_PALM`, it routes straight to the corresponding capture screen; otherwise it shows the face/palm chooser.
4. Capture → API auth → consent / MFA dialogs as required → card selection → either pay or return external tokens.
5. The Activity finishes. Your callback fires (on the main thread):
   * `onSuccess` — payment processed or external tokens returned
   * `onCancelled` — user tapped cancel, or you called `cancelPayment()`
   * `onFailure` — auth/payment failed and `returnOnFailure=true`, or a terminal error occurred

### Threading

* All callback methods are dispatched on the main thread.
* API calls (auth, payment, palm) run on a single-threaded executor inside the Activity; the Activity tears it down in `onDestroy`.
* Camera analysis runs on a dedicated analyzer executor; results are posted back to the main thread before any UI / API call.

### What gets torn down on Activity destroy

The SDK's customer-display Activity releases all biometric, network, and camera resources automatically when it finishes. You don't need to do anything in your own lifecycle to "clean up" the SDK between payments — just register a fresh callback before the next `startPayment`.

### Calling `startPayment` twice

`startPayment` overwrites the previously-registered callback. It does not serialize concurrent calls — only one biometric session can be in flight at a time. If you need to start a second payment after the first completes, wait for the callback to fire, then call `startPayment` again. There is no explicit "session id" — the `requestId` you supply is what threads back to your callback.

### Process death / re-entry

The order data and callback registration are process-scoped and do **not** survive process death. If the OS kills your app while the Activity is running, restart the flow from your POS — there is no resumable session.

***

## 7. Customer-facing display (dual-display devices)

On single-display devices you can ignore this section: pass `Display.DEFAULT_DISPLAY` (or omit `displayId`) and you're done.

On dual-display POS hardware (separate merchant + customer screens), the capture UI should land on the customer display. Two things must be true:

1. The Intent must include `FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_MULTIPLE_TASK` — `createNativeIntent(ctx, displayId)` adds these for you when `displayId >= 0`.
2. You **must** pass an `ActivityOptions` bundle. Flags alone do not target a display.

```kotlin
val displays = (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).displays
val customerDisplayId = displays.firstOrNull {
    it.displayId != Display.DEFAULT_DISPLAY && it.isValid
}?.displayId ?: Display.DEFAULT_DISPLAY

val intent = winkPay.createNativeIntent(this, customerDisplayId)
val opts   = ActivityOptions.makeBasic().setLaunchDisplayId(customerDisplayId)
startActivity(intent, opts.toBundle())
```

Without the options bundle you'll see `Failed to put TaskRecord on display N` in logcat and the Activity will land on the merchant display.

### Relaunching your own POS Activity on the customer display

If your POS UI also belongs on the customer display:

```kotlin
override fun onCreate(savedInstanceState: Bundle?) {
    super.onCreate(savedInstanceState)
    if (relaunchOnCustomerDisplayIfNeeded()) return
    // ... normal onCreate
}

private fun relaunchOnCustomerDisplayIfNeeded(): Boolean {
    if (intent.getBooleanExtra("__relaunched", false)) return false
    val dm = getSystemService(DisplayManager::class.java)
    val customer = dm.displays.firstOrNull {
        it.displayId != Display.DEFAULT_DISPLAY && it.isValid
    } ?: return false

    val current = if (Build.VERSION.SDK_INT >= 30) display?.displayId
                  else windowManager.defaultDisplay.displayId
    if (current == customer.displayId) return false

    val relaunch = Intent(this, javaClass).apply {
        putExtra("__relaunched", true)
        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_MULTIPLE_TASK)
    }
    startActivity(relaunch, ActivityOptions.makeBasic()
        .setLaunchDisplayId(customer.displayId).toBundle())
    finish()
    return true
}
```

Add `android:taskAffinity=".your_customer_task"` to your Activity in the manifest so Android grants a fresh task on the customer display.

The SDK's customer-display Activity is already configured to land on a fresh task — you do not need to configure it.

***

## 8. Palm capture details

The SDK runs palm capture on-device on the PalmID 2.0 core engine, including liveness verification. The full flow — including model build and identify call — is internal to the SDK; your host code only sees the eventual `onSuccess` or `onFailure` callback.

How the engine is driven is selected via `WinkPaySdk.Config(palmEngineDriver = ...)` (§4.1). The default `"session"` driver hands the camera to the palm engine's own capture session — the configuration with markedly better gesture-liveness reliability in low light. `"bridge"` restores the previous frame-fed pipeline (SDK-owned camera and exposure management) as a rollback knob. The capture UI, prompts, and callback contract are identical on both; the integrator-visible differences are called out below.

Anti-spoofing is selected via `WinkPaySdk.Config(palmLivenessMode = ...)` (§4.1):

* **`"hires"` (default)** — after the palm is captured, the engine analyzes a high-resolution still taken under the SDK's own screen fill light (the guide-frame interior brightens automatically). The customer just holds their palm steady — no gesture prompt.
* **`"gesture"`** — the engine prompts a randomized hand gesture (fist, thumbs-up, …) and verifies it was performed before the model is accepted. The prompt must be answered within a bounded window or the attempt fails.
* **`"both"`** — the gesture gate followed by the hires gate on the same capture.

**RGB+IR hardware.** If your device's palm camera is a vendor-supported RGB+IR module, set `WinkPaySdk.Config(enableIR = true)`: the engine then captures with liveness off entirely (no gesture prompt, no hires still — the IR/vein channel is the presentation-attack defense on that hardware), and palm identification runs in `rgb-ir` match mode so the backend matches on both the palm print and the vein image. `palmLivenessEnabled = false` is the same capture posture without the match-mode switch — evaluation use only. **Neither flag is safe on a plain RGB camera** (§4.1): it would remove the only anti-spoof layer.

The capture screen guides the user into the correct pose automatically — surfacing on-screen prompts to move lower / further back or to centre the hand in the bracket when it sits just outside the usable zone. No host configuration is involved beyond the mode flag.

Lighting is handled automatically on both drivers, to different depths. The default `"session"` driver boosts camera exposure for the palm and otherwise lets the engine manage its own camera. The `"bridge"` driver additionally runs the SDK's full lighting stack: camera exposure is metered on the palm (not the background), the area of the customer display *outside* the guide frame is dimmed when a bright background would otherwise overpower the palm, and if no palm is detected for a couple of seconds the guide-frame interior brightens (and screen brightness is raised) to act as a fill light for the hovering hand. Backlit scenes — a light source behind the hand — are detected and compensated for automatically. On the bridge driver, expect visible dimming/brightening of the customer display during palm capture; it is intentional and requires no host configuration.

A rejected liveness check does **not** fail the session immediately. On the default driver the attempt retries **in place**: the customer sees a short "let's try again" message on the capture screen (e.g. `"Gesture not matched — let's try again"`) and a fresh attempt starts with the camera still open, up to **4 attempts total**. Only when the final attempt is also rejected does the session fail, with the terminal message `"Palm liveness check failed. Please try again."` and `errorCode = "LIVENESS_FAILED"` if `returnOnFailure` is set; otherwise it routes to the SDK's retry screen. (On the `"bridge"` driver, liveness rejections fail on the first rejection with messages starting `"Liveness check…"` — e.g. `"Liveness check failed"`, `"Liveness check incomplete"`.)

When debugging false-rejects, enable SDK logging — `WinkPaySdk.Config(debugLogging = true)`, or per-tag via `adb shell setprop log.tag.<TAG> DEBUG` — and capture logcat with the palm tag for your driver: **`PalmIdSessionCapture`** for the default `"session"` driver, `PalmCaptureSession` for `"bridge"`. Every reject logs a one-line reason. Reach out to the Wink team if you believe these rejects require SDK tuning. (Debug/info logs are off by default.)

### 8.1 Lite palm path (server-assisted — not in current releases)

Earlier previews of this guide described an optional **server-assisted** palm capture path, selected via a `PALM_MODELING_SERVER=http://host:port` key in `Config.envFileContent`, that offloaded palm-model construction to an external modeling server. **That path does not ship in the 1.6.x AARs** — setting `PALM_MODELING_SERVER` has no effect, and all palm capture (model build and liveness) runs on-device as described above. If the server-assisted path returns in a future release, it will be re-documented here.

### 8.2 Palm engine packaging & ABI support

The palm engine ships as a separate Maven artifact, `com.palmid:palmid-core:2.0.0`, bundled inside the same distribution repo as `com.wink:winkpay-sdk` and declared as a dependency in its POM — Gradle resolves it automatically as long as your `repositories` block points at the unzipped `winkpay-sdk-repo` (§2). No extra integration step.

The engine's native libs are **arm64-v8a only** (~19 MB). The winkpay-sdk AAR additionally carries a tiny ABI stub for armeabi-v7a so the final APK stays installable on 32-bit POS hardware (PAX A920, Ingenico DX8000):

* **64-bit devices** — palm works normally.
* **32-bit devices** — the app installs and face flows work normally, but palm gates off: palm requests fail explicitly with `PALM_UNSUPPORTED` (§9.1), and the palm tiles/options are hidden. The SDK detects this at runtime and never crashes on a palm attempt.

There is now **one distribution** for all hardware — the former separate 32-bit (`-arm32`) zip is discontinued, because the palm engine has no armeabi-v7a build. The same deliverable installs on both 64-bit and 32-bit devices; palm simply gates off on 32-bit.

***

## 9. Error handling

### 9.1 `errorCode` values delivered via `EmbeddedPaymentError`

The native flow produces the following error codes:

| `errorCode`          | Cause                                                                                                                                                                                                                                                  |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `LIVENESS_FAILED`    | The biometric session failed because liveness was rejected (face or palm). The user-visible message contains `"liveness check"` — `"Liveness check failed"` for face (and the `"bridge"` palm driver), or `"Palm liveness check failed. Please try again."` for the default palm driver, after its 4 in-place attempts (§8). |
| `USER_NOT_ENROLLED`  | Face/palm capture succeeded but no enrolled user matched. Only delivered when the host opted out of in-SDK enrollment (e.g. check-in flows on merchants that don't offer lite registration). The accompanying message is a fixed "not enrolled" string. |
| `PROFILE_INCOMPLETE` | The matched user exists but their profile is missing fields the backend requires before continuing. Message is a fixed string from the SDK — the BE response itself is generic, so the SDK substitutes a clearer one.                                  |
| `PALM_UNSUPPORTED`   | A palm flow (`PAY_PALM`, `PAY`, or check-in with `biometricType = "palm"`) was requested on a device whose hardware does not support palm capture, **or** whose process bitness doesn't match the palm-engine ABI bundled in the SDK variant (see §8.2). Delivered immediately, before any UI is shown. Message: `"Palm biometrics aren't supported on this device. Please use face capture instead."` Hosts that want a face-only fallback should re-issue the request as `PAY_FACE`. |
| `PAYMENT_FAILED`     | The customer authenticated successfully but the charge itself failed — processor decline or payment-service error. The `errorMessage` carries the decline reason where the server supplies one (e.g. "Insufficient funds"). The in-SDK failure screen shows "Payment unsuccessful" for these instead of the authentication-failure copy. |
| `AUTH_FAILED`        | Any other terminal authentication failure: face not recognized, server error, network error, etc. The `errorMessage` carries the human-readable reason (server-supplied where available, otherwise face-friendly — see §9.2). |

`onFailure` is only called when:

* You set `EmbeddedPaymentRequest.returnOnFailure = true`, **or**
* A terminal error occurs that the SDK does not have a retry path for (e.g. network failure during a payment that already debited).

By default (`returnOnFailure = false`) the native UI shows a Retry / See Options screen and the user can re-attempt without your code being notified.

### 9.2 User-visible strings you may see in logcat / `errorMessage`

Not all of these surface to your callback (most route to the native retry screen unless `returnOnFailure = true`), but you may see them while debugging:

| Message prefix                           | Meaning                                                             |
| ---------------------------------------- | ------------------------------------------------------------------- |
| `Liveness check failed.`                 | Palm or face liveness rejected — wrong/absent gesture, gesture window expired, or a spoof guard triggered |
| `Liveness check incomplete`              | Palm left the frame mid-gesture (`"bridge"` palm driver)            |
| `Palm liveness check failed. Please try again.` | Palm liveness rejected on all 4 in-place attempts (default palm driver — §8) |
| `Palm not recognized. Please try again.` | Genuine no-match: the palm service compared the scan and found no enrolled user. Emitted ONLY when a match verdict actually arrived — infrastructure failures use the messages below |
| `Couldn't reach the server. Please check your connection and try again.` | Network transport error before a match verdict — connectivity, DNS, timeout |
| `The palm service is temporarily unavailable. Please try again in a moment.` | The palm service returned a server error (5xx) without a clean message — outage/deploy, not a biometric result |
| `Palm verification couldn't be completed. Please try again.` | Palm identify failed for a reason other than network/5xx (no match verdict arrived). A clean server-supplied message, when present, is passed through instead |
| `Face not recognized` / `User not found` | The captured face did not match any enrolled user for this merchant |
| `Payment declined`                       | Card-level decline from the processor                               |
| `Network error`                          | Network transport error reaching the auth or payment API            |

> All palm rows above surface to the host callback under `errorCode = AUTH_FAILED` (liveness rows under `LIVENESS_FAILED`) — the **message** is what distinguishes a genuine no-match from an infrastructure failure. Only the genuine no-match renders the "We couldn't recognize you" retry screen; the others show neutral retry copy.

**Humanized face-scan errors.** When the auth server returns an HTTP error with no useful body — common for HTTP 400 on an unusable face image (occluded, blank, oversized, malformed) — the SDK substitutes a face-specific message instead of surfacing the raw HTTP reason phrase ("Bad Request", "Unauthorized", "Internal Server Error", or a bare `HTTP NNN` fallback). The replacements are keyed off the HTTP class:

| HTTP class            | User-visible message                                                                                                                            |
| --------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| 400 (Bad Request)     | `"We couldn't read your face from that image. Make sure your face is fully visible, well-lit, and not blocked by hands, hats, or sunglasses, then try again."` |
| 401 (Unauthorized)    | `"Your session expired. Please start over."`                                                                                                    |
| 404 (Not Found)       | `"We couldn't find a matching account. Please try again or sign up."`                                                                           |
| 5xx (Server)          | `"We're having trouble on our end. Please try again in a moment."`                                                                              |
| Network / IO          | `"Couldn't reach the server. Please check your connection and try again."`                                                                      |
| Other                 | `"Face scan failed. Please try again."`                                                                                                         |

If the server *does* return a real, descriptive error body, the SDK surfaces that verbatim — the substitution only kicks in when the response body would otherwise be useless (empty, or a bare reason phrase). The substituted text appears in `EmbeddedPaymentError.errorMessage` when `returnOnFailure = true`, and on the native retry screen otherwise.

### 9.3 Initialization-time exceptions

These come out as standard Kotlin exceptions, not via the callback:

| Exception                                                               | Cause                                                                                                                 |
| ----------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| `IllegalStateException("WinkPaySdk not initialized")`                   | You accessed an API that requires init before calling `WinkPayEmbedded.init`                                          |
| `IllegalStateException("$key is not configured in .env")`               | A required credential (`CLIENT_ID`, `MERCHANT_CLIENT_SECRET`) was missing or blank — pass it via `Config.clientId` / `Config.merchantClientSecret` (or the legacy `envFileContent` keys) |
| `IllegalArgumentException("environment must be one of [qa, stage, prod]…")` | `Config.sdkEnvironment` was something other than `"qa"`, `"stage"`, or `"prod"`                                  |

### 9.4 Companion-app error codes

When integrating via the companion app (intent-based IPC) instead of the embedded SDK, you'll see an additional set of error codes — `INVALID_REQUEST`, `UNAUTHORIZED_CALLER`, `IDEMPOTENT_REJECT`, `PAYMENT_FAILED`, `NOT_IMPLEMENTED`. Those are documented in [`POS_INTEGRATION.md`](../../POS_INTEGRATION.md), which is the authoritative reference for that integration path.

***

## 10. Build notes & APK size

### 10.1 ProGuard / R8 — consumer rules are auto-propagated

The SDK ships `consumer-rules.pro` and declares it via `consumerProguardFiles`, so it is automatically merged into your app's R8 rules at build time. **You do not need to copy any WinkPay-specific rules into your app** — the bundled set covers the public API surface (`WinkPaySdk`, `WinkPaySdk.Config`, `WinkPayEmbedded`, the `EmbeddedPayment*` data classes) and the SDK's own serialization needs.

If you minify your app and see R8 warnings about _other_ transitive dependencies that **your own app** pulls in (your POS vendor SDK, analytics libraries, etc.), add the relevant `-keep` / `-dontwarn` rules in your own `app/proguard-rules.pro` — those live outside the WinkPay SDK and are your app's responsibility.

### 10.2 APK size impact

The SDK adds roughly **12 MB compressed** to your APK, almost entirely the palm engine (`com.palmid:palmid-core`, arm64-v8a `libPalmAPISaas.so` ~19 MB uncompressed — see §8.2). The `winkpay-sdk` AAR itself is ~2 MB. The face-detection model is delivered on-device at runtime by the platform — zero APK cost.

To keep the build lean:

* **64-bit POS hardware** — set `ndk.abiFilters += "arm64-v8a"`; including other ABIs just inflates your APK with the armeabi-v7a stub that carries no engine.
* **Mixed / 32-bit fleets** — set `ndk.abiFilters += listOf("arm64-v8a", "armeabi-v7a")` so the one APK installs everywhere; palm runs on the 64-bit devices and gates off on the 32-bit ones (§8.2).

### 10.3 Versioning

Maven coordinates are `com.wink:winkpay-sdk:<version>`. The current recommended version is **`1.7.9`**. The SDK versioning convention is:

* `1.x.0` — feature releases
* `1.x.y` — bug-fix releases

When you upgrade the SDK, swap the version in `app/build.gradle.kts` and copy the new local Maven repo over `vendor/winkpay-sdk/winkpay-sdk-repo`. No code changes are required for patch-level upgrades within the same major.

***

## 11. Troubleshooting

| Symptom                                                                                              | Likely cause                                                                                                                                                                                                                | Fix                                                                                                                                                                                                   |
| ---------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `IllegalStateException: WinkPaySdk not initialized`                                                  | API used before `WinkPayEmbedded.init()` returned                                                                                                                                                                           | Move `init` earlier (Application.onCreate or top of Activity.onCreate).                                                                                                                               |
| `IllegalStateException: CLIENT_ID is not configured in .env`                                         | `Config.clientId` (and `merchantClientSecret`) were not passed, and `envFileContent` was either absent or missing the key                                                                                                  | Pass `clientId` + `merchantClientSecret` directly on `Config` (see §5.1). For the legacy `.env` path, verify the file actually contains the keys and that `Config(envFileContent = …)` was set.       |
| Capture screen shows a red `!` "Device not registered" badge                                         | First-run device registration hasn't completed yet — the SDK couldn't reach the WinkPay platform, or `Config.deviceSerialNumber` was null / blank                                                                            | Confirm `Config.deviceSerialNumber` is non-blank; check network reachability. Once the device identifier is cached, the badge is gone for the lifetime of the install. See §5.2.                       |
| SDK init crashes with a native-library load error                                                    | Bundled native libs are being packaged compressed inside `base.apk`                                                                                                                                                         | Set `packaging.jniLibs.useLegacyPackaging = true` in your app's `build.gradle.kts`.                                                                                                                   |
| `Failed to put TaskRecord on display N` (then Activity lands on the merchant display)                | `startActivity` called without `ActivityOptions.setLaunchDisplayId(...)`                                                                                                                                                    | Pass `opts.toBundle()` as the second arg. Flags alone are insufficient — see §7.                                                                                                                      |
| Customer display shows a black preview; capture eventually times out or fails with `LIVENESS_FAILED` | Host app didn't request `android.permission.CAMERA` at runtime before launching the payment. There is no "permission denied" callback — the camera just opens to black and the biometric session never sees a usable frame. | Call `ActivityCompat.requestPermissions(activity, arrayOf(Manifest.permission.CAMERA), REQ_CODE)` before `winkPay.startPayment(...)`. Don't proceed to `createNativeIntent` until the user grants it. |
| Payment succeeds but `winkCardToken` / `transactionId` is null in the result                         | Mode confusion: `returnExternalTokens=true` populates `winkCardToken` and leaves `transactionId` null; `returnExternalTokens=false` does the opposite                                                                       | Read §4.7 — pick the mode that matches who is processing the charge.                                                                                                                                  |
| Palm liveness consistently fails for a real user on a real device                                    | The palm engine's liveness verifier is rejecting (or, on the `"bridge"` driver, one of the SDK's local liveness guards)                                                                                                     | Enable SDK logging (`Config(debugLogging = true)` or `adb shell setprop log.tag.<TAG> DEBUG`), then capture logcat with the palm tag for your driver — **`PalmIdSessionCapture`** (default `"session"` driver) or `PalmCaptureSession` (`"bridge"`). Each reject logs the precise reason. If you see false rejects in normal use, file a bug with the log excerpt. |
| `INSTALL_FAILED_INVALID_APK: Failed to extract native libraries, res=-2` on `adb install`            | Same root cause as the native-library load error above                                                                                                                                                                      | `useLegacyPackaging = true`.                                                                                                                                                                          |
| Palm requests fail with `PALM_UNSUPPORTED` | The device (or the process) is 32-bit — the palm engine is arm64-v8a only, so palm gates off and face-only remains available. Genuinely 32-bit POS hardware (PAX A920, Ingenico DX8000) always hits this | Expected on 32-bit hardware; there is no 32-bit palm engine. On a 64-bit device, ensure your app isn't forced into a 32-bit process (don't restrict `abiFilters` to armeabi-v7a only). See §8.2. |
