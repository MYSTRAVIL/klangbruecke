plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "klangbruecke.remote"
    compileSdk = 34

    defaultConfig {
        applicationId = "klangbruecke.remote"
        minSdk = 26
        targetSdk = 34
        versionCode = 4
        versionName = "0.3.3"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
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
    // No AndroidX at runtime: minSdk 26 gives us runtime-permission APIs on the platform Activity,
    // and org.json is part of the Android framework. Kept dependency-light on purpose.
    testImplementation("junit:junit:4.13.2")
    // The JVM unit test runs off-device, so it needs a real org.json on the test classpath
    // (on-device this class ships in android.jar). Same API either way.
    testImplementation("org.json:json:20231013")
}
