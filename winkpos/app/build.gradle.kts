import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Merchant credentials come from local.properties so they stay out of git.
val localProps = Properties().apply {
    val f = rootProject.file("local.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}

fun prop(key: String): String = localProps.getProperty(key)?.trim() ?: ""

android {
    namespace = "com.bloomingdales.winkpos"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.bloomingdales.winkpos"
        minSdk = 24
        targetSdk = 35
        versionCode = 1
        versionName = "1.0"

        // 64-bit POS hardware; armeabi-v7a fleets run face-only (palm gates off).
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a") }

        buildConfigField("String", "WINK_CLIENT_ID", "\"${prop("WINK_CLIENT_ID")}\"")
        buildConfigField(
            "String",
            "WINK_MERCHANT_CLIENT_SECRET",
            "\"${prop("WINK_MERCHANT_CLIENT_SECRET")}\"",
        )
        buildConfigField("String", "WINK_ENV", "\"${prop("WINK_ENV").ifEmpty { "stage" }}\"")

        // Register link: "" (standalone demo), "pxrrs" (PAX flow: REST to the
        // on-terminal PxRetailer service), "ws" (Wi-Fi, needs POS_LINK_WS_URL,
        // e.g. ws://192.168.1.50:8181/pos), or "pcl" (USB via JPxSerialServer).
        buildConfigField("String", "POS_LINK_MODE", "\"${prop("POS_LINK_MODE")}\"")
        buildConfigField("String", "POS_LINK_WS_URL", "\"${prop("POS_LINK_WS_URL")}\"")
        buildConfigField("String", "POS_LINK_PXRRS_URL", "\"${prop("POS_LINK_PXRRS_URL")}\"")

        // Okta/Auth0 rewards (user_metadata.points) — optional; the dashboard
        // falls back to the static demo points when unset.
        buildConfigField("String", "OKTA_DOMAIN", "\"${prop("OKTA_DOMAIN")}\"")
        buildConfigField("String", "OKTA_MTM_CLIENT_ID", "\"${prop("OKTA_MTM_CLIENT_ID")}\"")
        buildConfigField("String", "OKTA_CLIENT_SECRET", "\"${prop("OKTA_CLIENT_SECRET")}\"")
    }

    packaging {
        jniLibs {
            // The palm engine's native libs must extract to lib/<abi>/.
            useLegacyPackaging = true
        }
        resources {
            excludes += listOf(
                "META-INF/INDEX.LIST",
                "META-INF/io.netty.versions.properties",
                "META-INF/DEPENDENCIES",
                "META-INF/LICENSE*",
                "META-INF/NOTICE*",
            )
        }
    }

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("com.wink:winkpay-sdk:1.7.10")

    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-ktx:1.9.0")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
}
