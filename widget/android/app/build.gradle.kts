plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// GitHub's run number makes each build newer than the last, so Android installs it as an update.
val build = System.getenv("GITHUB_RUN_NUMBER")?.toIntOrNull() ?: 1

android {
    namespace = "net.verbaflow.speak"
    compileSdk = 35
    defaultConfig {
        applicationId = "net.verbaflow.speak"
        minSdk = 26
        targetSdk = 35
        versionCode = build
        versionName = "0.1.$build"
    }
    buildTypes {
        release { isMinifyEnabled = false }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    testOptions { unitTests.isReturnDefaultValues = true }
}

dependencies {
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    testImplementation("junit:junit:4.13.2")
}
