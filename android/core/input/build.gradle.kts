import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// Android library for the MotionEvent adapter; the gesture interpreter itself is plain Kotlin and is unit tested on the JVM
plugins {
    alias(libs.plugins.android.library)
    alias(libs.plugins.kotlin.android)
}

android {
    namespace = "app.nexscreen.core.input"
    compileSdk = 35
    buildToolsVersion = "35.0.0"

    defaultConfig {
        minSdk = 26
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    api(project(":core:network"))
    testImplementation(libs.junit)
}
