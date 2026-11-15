# DartTA – Google Play publishing kit

Everything needed to create the Google Play store entry for **DartTA**.

| | |
|---|---|
| App name | DartTA |
| Package (application ID) | `com.sta.dartta` |
| Version | 1.0.0 (version code 1) |
| Min / target SDK | 24 (Android 7.0) / 36 (Android 16) |
| AdMob app ID | `ca-app-pub-3964073132044809~2715564069` |
| AdMob banner unit | `ca-app-pub-3964073132044809/4346220831` (Release builds) |

## Folder contents

| Folder | Content |
|---|---|
| `release/` | `DartTA-1.0.0-1.aab` → upload to Play Console · `DartTA-1.0.0-1.apk` → signed universal APK for testing/sideloading |
| `listing/<lang>/` | `title.txt` (≤30), `short_description.txt` (≤80), `full_description.txt` (≤4000), `release_notes.txt` (≤500) for en-US, de-DE, es-ES |
| `graphics/` | `icon-512.png` (app icon 512×512), `feature-graphic-1024x500.png` |
| `screenshots/<lang>/` | 6 framed phone screenshots per language, 1080×1920 (9:16) – upload in order 01…06. `screenshots/raw/` = unframed emulator captures (working material, not uploaded) |
| `privacy/privacy-policy.html` | Privacy policy (EN + DE) – must be hosted at a public URL |
| `signing/` | **Upload key – back it up, never commit it** (git-ignored) |
| `tools/` | Helper scripts used to build / capture (can be deleted) |

## Building a release

```powershell
.\build-release.ps1                                   # version from DartForge.App.csproj
.\build-release.ps1 -VersionName 1.0.1 -VersionCode 2  # later updates
```

- The version code must increase with every upload to Play Console.
- Output goes to `PlayStore\release`. The script signs with `signing\dartta-upload.p12` (password in `signing\upload-key-password.txt`).
- Needs the Android SDK in an ASCII-only path: `C:\DartForge\AndroidSdk` with `platforms\android-35`, `platforms\android-36` and `build-tools\35.0.0`. API 35 is required by the `StreakCelebration.Maui` and `Sta.DataSync.Maui` libraries, API 36 by the app.
- Needs several GB free on drive C (NuGet packages, intermediate files in `C:\DartForge\.release-build`).

## AdMob configuration

IDs live in `DartForge.App/Platforms/Android/Ads/AdConfig.cs`; the app ID is also in `DartForge.App/Platforms/Android/AndroidManifest.xml` (both must match).

- **Release builds** use the real banner unit `ca-app-pub-3964073132044809/4346220831`.
- **Debug builds** use Google's test banner `ca-app-pub-3940256099942544/9214589741`, so you never click your own live ads while developing.
- Never click ads in a Release APK on your own phone – add it first in AdMob → Settings → Test devices.
- New AdMob apps and ad units may show few or no ads for the first hours to days.

## Before you upload – open items

1. **New Play Console app** – the package is `com.sta.dartta` (renamed from `com.dartforge.app`). Play Console cannot rename a package, so create a new app entry. If a draft for `com.dartforge.app` exists, it can be deleted.
2. **AdMob**
   - Create a GDPR consent message (AdMob → Privacy & messaging), otherwise the EU consent form is not shown.
   - After publishing, link the AdMob app to the Play Store listing of `com.sta.dartta` (AdMob → App settings → Add app store details).
3. **Google sign-in for cloud backup** – create an *Android* OAuth client in Google Cloud Console for `com.sta.dartta`, once with the upload-key SHA-1 `F6:06:E4:38:3E:9F:C3:19:F0:12:4B:7B:E3:CB:8B:67:C2:FD:EC:65` and once with the Play App Signing SHA-1 (Play Console → Test and release → App integrity, available after the first upload). Enable the Google Drive API and configure the OAuth consent screen (scope `drive.appdata`). Without this, sign-in/backup fails in the store build.
4. **Privacy policy** – the current text describes ads only. It must also cover the optional Google sign-in and the backup of match/training history to the user's own Google Drive (app data folder). Then host `privacy/privacy-policy.html` publicly (e.g. GitHub Pages, Google Sites) and paste the URL in Play Console → App content → Privacy policy.
5. **Store texts** – `listing/*/release_notes.txt` and `full_description.txt` do not yet mention streaks/celebrations and cloud backup. Optional, but worth adding.
6. **Target API** – the app targets API 36 (required since 31 Aug 2026) but is built with .NET 9 (bindings for API 35). Test on an Android 16 device/emulator before rolling out to production; the long-term fix is moving to .NET 10 (`net10.0-android`).
7. **32-bit devices** – .NET 9 builds arm64-v8a + x86_64 only, so old 32-bit-only phones won't see the app. Add `<RuntimeIdentifiers>android-arm;android-arm64;android-x64</RuntimeIdentifiers>` if you need them.
8. **Clean-up** – delete `signing/keystore-password.txt` (leftover from an aborted first attempt, belongs to no keystore; the real password is `signing/upload-key-password.txt`). Also safe to delete: `task.done` in the repo root and the `tools/` folder.

## Play Console walkthrough

1. **Create app** – name *DartTA*, default language English (US), App, Free.
2. **Test and release → Internal testing (or Production)** → Create release → accept **Play App Signing** → upload `release/DartTA-1.0.0-1.aab` → paste `listing/en-US/release_notes.txt` (and the de/es versions).
   - New personal developer accounts must run a **closed test with ≥12 testers for 14 days** before production access.
3. **Main store listing** – copy title, short and full description per language (Add translations → German (Germany), Spanish (Spain)); upload `graphics/icon-512.png`, `graphics/feature-graphic-1024x500.png` and 2–8 phone screenshots from `screenshots/<lang>/`.
4. **Store settings** – Category: *Apps → Sports*; contact e-mail.
5. **App content**
   - Privacy policy: URL from open item 4.
   - Ads: **Yes, my app contains ads.**
   - App access: all functionality available without special access (cloud backup is optional and uses the reviewer's own Google account).
   - Content rating: questionnaire → category *Utility/Productivity/Other*; answer *No* to violence, sexuality, language, controlled substances, gambling (darts scoring has no real-money gambling), user interaction/chat *No*, shares location *No*. Expected rating: Everyone / PEGI 3.
   - Target audience: **13+** (do not include under-13 – the app uses standard AdMob, not the Families ads program).
   - Advertising ID: **Yes** – used for *Advertising or marketing* (AdMob).
   - Government / financial / health / news: No.
   - **Data safety** – caused by the Google Mobile Ads SDK:

     | Data type | Collected | Shared | Purpose | Optional? |
     |---|---|---|---|---|
     | Location → Approximate location | Yes | Yes | Advertising or marketing | No |
     | Device or other IDs | Yes | Yes | Advertising or marketing, Analytics, Fraud prevention | No |
     | App activity → App interactions | Yes | Yes | Advertising or marketing, Analytics | No |
     | App info and performance → Crash logs, Diagnostics | Yes | Yes | Analytics, Fraud prevention | No |

     Data encrypted in transit: **Yes** · Data processed ephemerally: No.

     **Review before submitting:** the optional cloud backup (Google sign-in, match/training history stored in the user's own Google Drive) is not covered by the table above. Check Google's Data safety guidance for it – it typically adds *App activity → Other user-generated content* (app functionality, optional), plus *Personal info → Email address* only if the app reads or shows the account e-mail (the code requests only the `drive.appdata` scope) – and decide whether users can request deletion (they can delete the backup from their Drive).
6. Send for review.

## Verified

Release built 2026-11-15 16:30 (`DartTA-1.0.0-1.aab` 38.1 MB, `DartTA-1.0.0-1.apk` 38.6 MB):
- Package `com.sta.dartta`, label DartTA, versionCode 1, versionName 1.0.0, minSdk 24, targetSdk 36, not debuggable (APK via `aapt2`, AAB manifest checked).
- AdMob `APPLICATION_ID` = `ca-app-pub-3964073132044809~2715564069` in APK and AAB; the Release-compiled `DartForge.App.dll` (arm64 + x64) contains the real banner unit `ca-app-pub-3964073132044809/4346220831` and not the test banner.
- AAB and APK signed with the upload key (CN=DartTA, valid until 2054; SHA-256 `4E:15:9D:AC:6C:61:79:66:72:33:BB:45:30:97:22:CF:53:1E:7F:5C:12:A6:94:3A:AA:10:59:C9:19:57:81:2C`, SHA-1 `F6:06:E4:38:3E:9F:C3:19:F0:12:4B:7B:E3:CB:8B:67:C2:FD:EC:65`); APK has v2/v3 signatures.
- **Not yet tested on a device/emulator.** Install the APK (uninstall any old `com.dartforge.app` build first), play a match, check that the banner and the EU consent form appear, and try cloud backup once the OAuth client exists.
