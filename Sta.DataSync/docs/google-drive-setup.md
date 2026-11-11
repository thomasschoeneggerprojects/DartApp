# Google Drive setup

The provider stores data in the hidden **app data folder** (`appDataFolder`) with the least-privilege scope
`https://www.googleapis.com/auth/drive.appdata`. The user's normal Drive files are neither visible nor accessible.

> `appDataFolder` belongs to the Google Cloud **project** of the OAuth client. All platforms of one app (Windows,
> Android, iOS) must use OAuth clients of the **same project** to see the same data. Separately registered apps never
> share an app data folder, even for the same user. Different apps in one project are isolated by
> `DataSyncOptions.ApplicationId` (`Sta.DataSync/{ApplicationId}/…`).

## 1. Project and consent screen

1. <https://console.cloud.google.com/> → create a project (one per product family that should share data).
2. **APIs & Services → Library** → enable **Google Drive API**.
3. **OAuth consent screen**: user type *External*, app name, support e-mail; add the scope `.../auth/drive.appdata`
   (non-sensitive). While the app is in *Testing*, add test users; publish it for general use.

## 2. OAuth clients

| Platform | Client type | Settings | Used by |
|---|---|---|---|
| Windows (desktop, MAUI Windows, console) | **Desktop app** | none; loopback redirect `http://127.0.0.1:{port}/` is allowed automatically | `PkceGoogleAuthenticator` + `LoopbackOAuthBrowser` |
| Android | **Android** | package name (e.g. `com.sta.datasync.sample`) and SHA-1 of every signing certificate (debug and release) | `AndroidGoogleAuthenticator` (Google Identity Services) |
| iOS | **iOS** | bundle id; redirect `com.googleusercontent.apps.{id}:/oauth2redirect` (register the URL scheme in `Info.plist`) | `PkceGoogleAuthenticator` + `WebAuthenticatorOAuthBrowser` (untested) |

Debug SHA-1 on Windows:

```bash
keytool -list -v -keystore "%LOCALAPPDATA%\Xamarin\Mono for Android\debug.keystore" -alias androiddebugkey -storepass android -keypass android
```

For Play-distributed apps also add the SHA-1 of the Play app signing key (Play Console → App integrity).

The desktop client secret is required by Google for the code exchange but is not confidential in installed apps
(RFC 8252). Still keep it out of source control: pass it through configuration, environment variables or
`oauth.props` (samples).

## 3. Samples

**Console** (Windows):

```bash
set STA_DATASYNC_GOOGLE_CLIENT_ID=xxxx.apps.googleusercontent.com
set STA_DATASYNC_GOOGLE_CLIENT_SECRET=yyyy
dotnet run --project samples/Sta.DataSync.Console.Sample -- signin
dotnet run --project samples/Sta.DataSync.Console.Sample -- sync --folder C:\Temp\synced
```

Tokens are stored DPAPI-encrypted in `%LOCALAPPDATA%\Sta.DataSync.ConsoleSample\google-tokens.bin`.

**MAUI** (Android + Windows): copy `samples/Sta.DataSync.Maui.Sample/oauth.example.props` to `oauth.props`
(git-ignored) and fill in the desktop client. Android needs no value: register an Android client for
`com.sta.datasync.sample` with your debug SHA-1. Without configuration the app still runs offline and reports the
missing configuration when you press *Connect*.

## 4. Opt-in tests against the real API

`tests/Sta.DataSync.IntegrationTests/RealGoogleDriveTests` runs only when these variables are set (use a test account):

| Variable | Value |
|---|---|
| `STA_DATASYNC_GOOGLE_CLIENT_ID` | desktop client id |
| `STA_DATASYNC_GOOGLE_CLIENT_SECRET` | desktop client secret |
| `STA_DATASYNC_GOOGLE_REFRESH_TOKEN` | refresh token for scope `drive.appdata` (e.g. from the console sample's sign-in, or the OAuth Playground with your own client) |

The test synchronizes a temporary folder between two simulated devices under a random application id and deletes its
remote files afterwards.

## Provider behavior

- Logical paths are file names in `appDataFolder`; an app property (`staScope` = root/application/scope) allows
  server-side filtering. Listings are paged (`PageSize`, max 1000).
- Uploads use resumable sessions in `UploadChunkSize` chunks (multiple of 256 KB); an upload interrupted by process
  termination restarts on the next run (content-addressing makes this safe).
- Downloads are streamed (`alt=media`), never buffered completely.
- Change detection: Drive change tokens (`changes.list`, space `appDataFolder`); an expired token falls back to a full
  listing.
- Errors: 401 → one forced token refresh, then `SyncAuthenticationException`; 403 `rateLimitExceeded`/
  `userRateLimitExceeded`, 429, 5xx, network → `SyncTransientException`; 403 `storageQuotaExceeded` →
  `SyncProviderException`; 404 → `SyncItemNotFoundException`.
- No conditional writes (Drive v3 limitation): the engine uses the verified revision log (see architecture).
