# DartTA – Google Play publishing kit

Everything needed to create the store entry for **DartTA** (`com.dartforge.app`, version 1.0.0 / code 1).

| Folder | Content |
|---|---|
| `release/` | `DartTA-1.0.0-1.aab` → upload to Play Console · `DartTA-1.0.0-1.apk` → signed universal APK for testing/sideloading |
| `listing/<lang>/` | `title.txt` (≤30), `short_description.txt` (≤80), `full_description.txt` (≤4000), `release_notes.txt` (≤500) for en-US, de-DE, es-ES |
| `graphics/` | `icon-512.png` (app icon 512×512), `feature-graphic-1024x500.png` |
| `screenshots/<lang>/` | 6 framed phone screenshots per language, 1080×1920 (9:16) – upload in order 01…06. `screenshots/raw/` = unframed emulator captures (working material) |
| `privacy/privacy-policy.html` | Privacy policy (EN + DE) – must be hosted at a public URL |
| `signing/` | **Upload key – back it up, never commit it** (git-ignored) |
| `tools/` | Helper scripts used to build / capture (can be deleted) |

Rebuild later with `.\build-release.ps1 -VersionName 1.0.1 -VersionCode 2` (the version code must increase with every upload).

## Before you upload – open items

1. **AdMob IDs** – the build still uses Google's *test* IDs (test ads, no revenue). Create an app + banner unit at admob.google.com, then replace the IDs in `DartForge.App/Platforms/Android/Ads/AdConfig.cs` **and** `AndroidManifest.xml`, rebuild with a higher version code. Also create a GDPR consent message (AdMob → Privacy & messaging), otherwise the EU consent form is not shown.
2. **Privacy policy URL** – host `privacy/privacy-policy.html` publicly (e.g. GitHub Pages, Google Sites) and paste the URL in Play Console → App content → Privacy policy. Check the contact e-mail in it.
3. **Target API** – the app targets API 36 (required since 31 Aug 2026) while still built with .NET 9 (compiled against API 35). Test on an Android 16 device/emulator before rolling out to production; the long-term fix is moving to .NET 10 (`net10.0-android`).
4. **32-bit devices** – .NET 9 builds arm64-v8a + x86_64 only, so old 32-bit-only phones won't see the app. Fine for most users; add `<RuntimeIdentifiers>android-arm;android-arm64;android-x64</RuntimeIdentifiers>` if you need them.
5. Delete `signing/keystore-password.txt` – leftover from an aborted first attempt, belongs to no keystore. The real password is `signing/upload-key-password.txt`. Also safe to delete: `task.done` in the repo root and the `tools/` folder.

## Play Console walkthrough

1. **Create app** – name *DartTA*, default language English (US), App, Free.
2. **Test and release → Production (or start with Internal testing)** → Create release → accept **Play App Signing** → upload `release/DartTA-1.0.0-1.aab` → paste `listing/en-US/release_notes.txt` (and the de/es versions).
   - New personal developer accounts must run a **closed test with ≥12 testers for 14 days** before production access.
3. **Main store listing** – copy title, short and full description per language (Add translations → German (Germany), Spanish (Spain)); upload `graphics/icon-512.png`, `graphics/feature-graphic-1024x500.png` and 2–8 phone screenshots from `screenshots/`.
4. **Store settings** – Category: *Apps → Sports*; contact e-mail.
5. **App content**
   - Privacy policy: URL from step 2 above.
   - Ads: **Yes, my app contains ads.**
   - App access: all functionality available without special access.
   - Content rating: questionnaire → category *Utility/Productivity/Other*; answer *No* to violence, sexuality, language, controlled substances, gambling (darts scoring has no real-money gambling), user interaction/chat *No*, shares location *No*. Expected rating: Everyone / PEGI 3.
   - Target audience: **13+** (do not include under-13 – the app uses standard AdMob, not the Families ads program).
   - Advertising ID: **Yes** – used for *Advertising or marketing* (AdMob).
   - Government / financial / health / news: No.
   - **Data safety** (caused by the Google Mobile Ads SDK; the app itself sends nothing):

     | Data type | Collected | Shared | Purpose | Optional? |
     |---|---|---|---|---|
     | Location → Approximate location | Yes | Yes | Advertising or marketing | No |
     | Device or other IDs | Yes | Yes | Advertising or marketing, Analytics, Fraud prevention | No |
     | App activity → App interactions | Yes | Yes | Advertising or marketing, Analytics | No |
     | App info and performance → Crash logs, Diagnostics | Yes | Yes | Analytics, Fraud prevention | No |

     Data encrypted in transit: **Yes** · Users can request deletion: **No** (no account; game data stays on the device and is removed on uninstall) · Data processed ephemerally: No.
6. Send for review.

## Verified
- AAB and APK signed with the upload key (CN=DartTA, valid until 2054; SHA-256 `4E:15:9D:AC:6C:61:79:66:72:33:BB:45:30:97:22:CF:53:1E:7F:5C:12:A6:94:3A:AA:10:59:C9:19:57:81:2C`), APK has v2/v3 signature.
- Manifest: `com.dartforge.app`, versionCode 1, versionName 1.0.0, minSdk 24, targetSdk 36, not debuggable, AD_ID permission declared.
- Release APK installed and played on the Android 14 emulator (X01 vs DartBot, Cricket, training) in English, German and Spanish – no crashes.
