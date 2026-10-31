# DartForge - offline darts for Android

.NET 8 MAUI Blazor Hybrid source project. See `IMPLEMENTATION_STATUS.md` for a candid feature matrix.

## Build on Windows

1. Install Visual Studio 2022 with **.NET Multi-platform App UI development** workload and .NET 8 Android tools.
2. From a developer terminal: `dotnet workload install maui-android`
3. Build a signed ARM64 debug APK with `./build-android.ps1` (or select another ABI with `-RuntimeIdentifier android-x64`, for example).
4. Connect an Android device with USB debugging and use `./build-android.ps1 -Run` to build for the device's ABI, install, and launch it. If multiple devices are connected, pass `-Device <adb-serial>`.
5. Run unit tests: `dotnet test DartForge.Tests/DartForge.Tests.csproj`.

The Android project is configured for API 34 and has been build- and launch-tested on an Android emulator.

## Architecture
- `DartForge.Core`: pure .NET 8 game logic / basic DartBot
- `DartForge.App`: MAUI Android Blazor Hybrid user interface + SQLite persistence
- `DartForge.Tests`: starter tests

The app shows a Google AdMob banner (INTERNET permission) with Google UMP consent for EU users. Game play stays fully offline. Replace the Google test ids in `DartForge.App/Platforms/Android/Ads/AdConfig.cs` and `AndroidManifest.xml` before publishing.
