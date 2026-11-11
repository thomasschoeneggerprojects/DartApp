# Operations

## Security

- **Scopes:** only `drive.appdata` by default; the app cannot read or change the user's other Drive files.
- **Flows:** authorization code with PKCE (S256) and `state` validation for desktop/iOS; Google Identity Services on
  Android (no refresh token or secret in the app). All endpoints are HTTPS.
- **Tokens:** stored only through `IGoogleTokenStore` implementations backed by platform protection
  (`MauiSecureTokenStore` → Android Keystore / iOS Keychain / Windows; `ProtectedFileTokenStore` → Windows DPAPI).
  Tokens never appear in logs, exceptions or results (`GoogleTokenSet.ToString()` hides them). Sign-out revokes the
  grant and deletes local tokens.
- **No secrets in source:** client ids/secrets come from configuration, environment variables or git-ignored files.
- **Integrity:** every download is verified (length and SHA-256) before it can replace local data; SQLite snapshots
  and downloads pass `integrity_check`; manifests are size-limited and validated (ids, hashes, schema version).
- **Paths:** `SyncPath` rejects absolute paths, `..`, backslashes and control characters; file sources additionally
  verify that every resolved path stays below the registered root. Nothing outside registered sources is touched.
- **Encryption at rest beyond Google's:** not active in version 1 (see `ISyncContentTransform`).

## Platform limitations

| Platform | Status |
|---|---|
| Windows (console, WPF, MAUI) | Built and unit/integration tested; MAUI sample starts and runs offline. Real Google sign-in not exercised in this repository. |
| Android (MAUI) | Built. Google Identity Services flow requires a configured Android OAuth client; not run on a device here. Sync pauses in the background (no background service). |
| iOS (MAUI) | Builds; `WebAuthenticator` + PKCE path implemented but untested. |
| Google Drive | No conditional writes (revision-log protocol); uploads resume within a run only; listing consistency is assumed for commit verification (lost commits are detected and re-planned on the next run). |
| SQLite | Whole-database granularity; database conflicts need an explicit decision; restore needs a moment without a writer holding the lock. |
| Clocks | Never used for change detection; only `LatestWins` depends on them. |

## Logging and diagnostics

Structured events via `ILogger` (source-generated `LoggerMessage`), each run in a scope with `SyncSessionId` and
`SyncScope`:

| Event id | Event |
|---|---|
| 1001 / 1002 / 1003 / 1004 | run started / finished (counts, bytes, duration) / canceled / failed |
| 1010 / 1011 / 1012 | local scan / local changes detected / remote changes detected |
| 1020 / 1021 / 1022 | transfer started / operation failed / execution summary |
| 1030 | transient failure, retry scheduled |
| 1040 | lost a concurrent commit, re-planning |
| 1050 | conflict detected |
| 1060 / 1061 / 1062 | recovering interrupted session / own commit lost after the fact / provider changed |
| 1070 | authentication failure |
| 2001 / 2002 | state database corrupt and recreated / migrated |
| 3001 / 3100 | Google authentication failure / Drive request (debug) |
| 4001–4005 | scheduler started, paused, retry scheduled, connectivity, stopped |

`SyncResult.SessionId` matches the log scope, so a user-visible error can be correlated with logs.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `AuthenticationRequired` | Not signed in, token revoked or consent missing. Connect again; then call `ISyncScheduler.NotifyAuthenticationChanged()`. |
| `redirect_uri_mismatch` on Windows | The client is not of type *Desktop app*. |
| Android sign-in fails with `DEVELOPER_ERROR` / code 10 | Package name or SHA-1 of the signing certificate missing in the Android OAuth client. |
| Devices do not see each other's data | Clients of different Google Cloud projects (each project has its own app data folder) or different `ApplicationId`. |
| `SyncConfigurationException` at startup | Ambiguous provider (set `ProviderId`), read-only source in a download mode, `Custom` strategy without resolver, invalid options. |
| Conflicts on first sync of a new device | Both sides already had different content and no common baseline: expected. Decide, or choose a strategy. |
| "manifest uses schema N" | Another device runs a newer version; update the app. Nothing was overwritten. |
| Repeated `SyncConcurrencyException` failures | Another device commits constantly or local files change during upload; the run reports it and retries later. |
| State database recreated (log 2001) | It was corrupt and moved aside (`*.corrupt-*`). The next run compares from scratch; equal content is adopted, differences become conflicts. |
