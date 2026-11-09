# DartTA - offline darts for Android

.NET 9 MAUI Blazor Hybrid source project. See `IMPLEMENTATION_STATUS.md` for a candid feature matrix.

## Build on Windows

1. Install Visual Studio 2022 with **.NET Multi-platform App UI development** workload and .NET 9 Android tools.
2. From a developer terminal: `dotnet workload install maui-android`
3. Build a signed ARM64 debug APK with `./build-android.ps1` (or select another ABI with `-RuntimeIdentifier android-x64`, for example).
4. Connect an Android device with USB debugging and use `./build-android.ps1 -Run` to build for the device's ABI, install, and launch it. If multiple devices are connected, pass `-Device <adb-serial>`.
5. Run unit tests: `dotnet test DartForge.Tests/DartForge.Tests.csproj`.

The Android project targets API 35 and packages native libraries with 16 KB alignment for current Android devices.

## Architecture
- `DartForge.Core`: pure .NET 8 game logic / basic DartBot
- `DartForge.App`: MAUI Android Blazor Hybrid user interface + SQLite persistence
- `DartForge.Localization`: UI languages (English, German, Spanish) – `.resx` resources + `ILocalizationService`
- `DartForge.Tests`: engine and localization tests

## Localization
- Strings live in `DartForge.Localization/Localization/Resources.resx` (English, default/fallback), `Resources.de.resx`, `Resources.es.resx`. Keys: `<View>_<Name>`, shared ones `Common_*`.
- First start follows the OS language (`de-AT` → German, `fr-FR` → English); Settings → Language overrides it (saved in MAUI Preferences, key `dartta.language`). Switching is instant, also during a match.
- Razor components inherit `LocalizedComponentBase` (`L("Key")`, `L("Key", args)`) and re-render on `LanguageChanged`. The engine only emits `GameMessageCode`s; `GameText` turns them into text.
- **Add a language:** add a value to `AppLanguage`, an entry in `SupportedLanguages.All`, a `Resources.xx.resx` with every key, and the code in `<SatelliteResourceLanguages>` of `DartForge.Localization.csproj`. The resource tests fail until every key and placeholder is present.

The app shows a Google AdMob banner (INTERNET permission) with Google UMP consent for EU users. Game play stays fully offline. Replace the Google test ids in `DartForge.App/Platforms/Android/Ads/AdConfig.cs` and `AndroidManifest.xml` before publishing.
