# Android Platform

Source package: `org.mass4.turian.android`.

Exports Turian games as Android CoreCLR applications (.NET 11): an APK, plus an App Bundle for release builds, with the game's content read in place from the APK, touch input, safe areas, the activity lifecycle and logcat logging. Desktop export ships with the editor; this brick adds Android to a project that wants it.

## Install

Add the brick to the project's `Bricks/manifest.json` from the brick registry, or from a local checkout:

```json
"org.mass4.turian.android": "file:../../Bricks/org.mass4.turian.android"
```

Then add a build profile whose platform is Android, and set Player Settings → Application Identifier and the Android Player Settings version code.

## Tools

- The .NET 11 SDK with its Android workload: `dotnet workload install android`.
- The Android SDK and a JDK 17 or newer. Both are found without environment variables: ANDROID_HOME and ANDROID_SDK_ROOT, JAVA_HOME, Android Studio's folders and its bundled JDK, then installed JDKs. To use others, set them in Studio under Settings → Build → Android; the CLI reads the same per-user file.

## Export and run on a device

- `turian export <project> --profile Android` builds the APK under `Output/Android/<game>`.
- `turian export … --install [--device <serial>]` also installs it on a connected device and starts it.
- `turian deploy <project> --profile Android [--device <serial>]` exports, installs, starts the game and shows its log until it exits; Ctrl+C stops it.
- `turian devices <project>` lists the devices ready to install on.
- In Studio, Build & Run with an Android profile exports, installs and starts the game; Settings → Build → Android → Install on Device does the same after every Android export.

Development profiles package the Khronos Vulkan validation layer when the optional `org.mass4.turian.android.validation.arm64` or `.x64` brick is installed.

## Develop the brick

The projects build against a Turian checkout beside the Bricks folder (`../../Turian`), or the one `TurianDir` names. `Tests~/Turian.Android.Tests` holds the tests; `Tools~/android-smoke.sh` exports a new project the way CI does, `Tools~/android-lifecycle.sh` runs the lifecycle runbook, and `Tools~/android-native.cs` rebuilds the SDL natives in `Native~`.
