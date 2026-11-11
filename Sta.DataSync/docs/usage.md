# Usage

## Registration (Microsoft DI)

| Method | Package | Registers |
|---|---|---|
| `AddDataSync(configure, stateDatabasePath?)` | Hosting | `IDataSyncService`, `ISyncStateStore` (SQLite), `SyncActivityState`, validated `DataSyncOptions` |
| `AddFileSyncSource(id, directory, configure?)` | Hosting | a scope for a directory |
| `AddSqliteSnapshotSyncSource(id, databasePath, configure?)` | Hosting | a scope for a SQLite database |
| `AddSyncSource(id, factory, scope?)` | Hosting | a scope for any `ISyncDataSource` |
| `AddConflictResolver<T>()` | Hosting | `IConflictResolver` for `ConflictResolutionStrategy.Custom` |
| `AddDataSyncScheduler(configure?)` / `AddDataSyncHostedScheduler(...)` | Hosting | `ISyncScheduler` (manual start / generic host) |
| `AddGoogleDriveSyncProvider(configure?)` | GoogleDrive | `ISyncProvider` with id `google-drive` |
| `AddGoogleDesktopAuthentication(configure)` | GoogleDrive | PKCE loopback sign-in (`IGoogleAuthenticator`) |
| `AddGoogleDriveMauiAuthentication(configure)` / `UseDataSync()` | Maui | platform sign-in, secure token store, connectivity, lifecycle |

The default state database is `{LocalApplicationData}/Sta.DataSync/{ApplicationId}/sync-state.db3`; MAUI apps should
pass a path below `FileSystem.AppDataDirectory`.

### Providers per scope

Every scope has exactly one destination. `SyncScopeOptions.ProviderId` selects it; when it is not set, the only
registered provider is used. With several providers registered, every scope must name its provider — data is never
copied to all providers implicitly. There is no switch over provider types: providers are resolved by id from DI.

## Options

`DataSyncOptions`: `ApplicationId` (required, one path segment), `RootFolder` (`Sta.DataSync`), `DefaultMode`,
`DefaultConflictStrategy`, `DefaultDeletionPolicy`, `MaxParallelTransfers` (1–16), `Retry` (`MaxAttempts`, `BaseDelay`,
`MaxDelay`), `CommitVerificationDelay`, `MaxCommitAttempts`, `KeepManifestRevisions`, `CleanupInterval`,
`ObjectGracePeriod`, `TombstoneRetention`, `MaxManifestBytes`. `Validate()` lists all problems; invalid options fail
at resolution (`OptionsValidationException`) or construction (`SyncConfigurationException`).

## Data sources

**Files.** `FileSyncSource` synchronizes files below a root directory. Ids are relative paths with `/`. Include and
exclude glob patterns (`*`, `**`, `?`). Paths that leave the root are rejected. Writes go to `.{name}.{random}.sta-tmp`
in the target directory, are flushed to disk, checked (hash, then the local version has not changed since planning) and
moved over the target. `WatchForChanges` raises change notifications for the scheduler.

**SQLite.** `SqliteSnapshotSyncSource` synchronizes a whole database as one item. Snapshots use the SQLite online
backup API (consistent, includes committed WAL content, while the app keeps writing) and are created only when the
database fingerprint changed. Snapshots and downloads must pass `PRAGMA integrity_check`. Restores use the backup API
into the live database, guarded by `BeforeRestoreAsync`/`AfterRestoreAsync`; a busy database makes the restore fail
transiently and it is retried later. The source cannot delete the database and cannot hold conflict copies, so
database conflicts are reported for an explicit decision.

## Modes and deletions

| Mode | Local change | Remote change | Both changed |
|---|---|---|---|
| `UploadOnly` | uploaded | skipped (reported) | conflict |
| `DownloadOnly` | replaced by remote (mirror) | applied | conflict (resolution limited to download direction) |
| `Bidirectional` | uploaded | applied | conflict |

| Deletion | `DeletionPolicy.Propagate` | `DeletionPolicy.Ignore` |
|---|---|---|
| Local item deleted (had a baseline) | remote tombstone (`UploadOnly`, `Bidirectional`) | kept remotely |
| Remote tombstone | local item deleted (`DownloadOnly`, `Bidirectional`) | kept locally |
| Remote item missing, no tombstone | never a deletion: re-uploaded if the mode uploads, otherwise kept locally | same |
| Delete on one side, edit on the other | conflict — the edited content is kept | same |

## Conflicts

Configure per scope (`SyncScopeOptions.ConflictStrategy`) or globally (`DataSyncOptions.DefaultConflictStrategy`).
Open conflicts are in `SyncResult.Conflicts` and `IDataSyncService.GetConflictsAsync()`; record a decision with
`ResolveConflictAsync(scopeId, itemId, ConflictResolution.KeepLocal | KeepRemote | KeepBoth | Defer)`, applied by the
next run. Custom logic: implement `IConflictResolver` and register it with `AddConflictResolver<T>()`.

## Scheduling

`SyncScheduleOptions`: `SyncOnStartup`, `Interval` (zero disables), `SyncOnLocalChanges` + `ChangeDebounce`,
`WifiOnly`, `SyncOnConnectivityRestored`, `RetryBaseDelay`, `RetryMaxDelay`.

- `ISyncScheduler.StartAsync/StopAsync`, `PauseAutomaticSync/ResumeAutomaticSync`, `NotifyLocalChange`,
  `NotifyAuthenticationChanged`, `SyncNowAsync` (manual; ignores pause and Wi-Fi restriction).
- Transient failures are retried with exponential backoff and jitter; authentication failures stop automatic runs
  until `NotifyAuthenticationChanged`; permanent failures wait for the next regular trigger.
- On MAUI, `UseDataSync()` starts the scheduler on the first resume and pauses it in the background. No background
  service is assumed on Android or iOS.

## Results and errors

`SyncResult`: `Status` (`Succeeded`, `NoChanges`, `PartiallySucceeded`, `Failed`, `Canceled`,
`AuthenticationRequired`), counts (uploaded, downloaded, deleted, skipped, conflicts), `TransferredBytes`,
`StartedUtc`/`CompletedUtc`, `SessionId` (also in all log entries), `Failures` (with `IsTransient`), `Operations`,
`ScopeResults` (aggregate runs). Progress: `IProgress<SyncProgress>` or `IDataSyncService.ProgressChanged`.

Exceptions derive from `SyncException`: `SyncTransientException` (retry), `SyncAuthenticationException` (sign in),
`SyncConcurrencyException` (re-plan), `SyncIntegrityException` (verification failed, nothing replaced),
`SyncItemNotFoundException`, `SyncProviderException` (permanent destination error), `SyncConfigurationException`.
Provider exceptions are kept as `InnerException`.

## Public API overview

| Type | Purpose |
|---|---|
| `IDataSyncService` | `SynchronizeAsync` (all / one scope, with progress), `GetStatusAsync`, `GetConflictsAsync`, `ResolveConflictAsync`, events |
| `ISyncScheduler` | automatic synchronization control |
| `ISyncDataSource`, `ISyncSnapshotSource`, `ISyncChangeTracking` | data source contracts |
| `ISyncProvider`, `IRemoteChangeFeed` | destination contracts |
| `ISyncStateStore` | metadata persistence |
| `IConflictResolver`, `ISyncContentTransform` | extension points |
| `IConnectivityMonitor` | platform connectivity |
| `SyncPath`, `ContentHash` | path rules and hash format |
| `SyncScopeDefinition` | a scope (for registering without the Hosting helpers) |
