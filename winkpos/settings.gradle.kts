pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.PREFER_SETTINGS)
    repositories {
        google()
        mavenCentral()

        // WinkPay SDK (vendored local Maven repo, shipped in the SDK zip).
        // Contains com.wink:winkpay-sdk and its transitive palm engine
        // com.palmid:palmid-core.
        maven { url = uri("${rootDir}/vendor/winkpay-sdk/winkpay-sdk-repo") }
    }
}

rootProject.name = "winkpos"
include(":app")
