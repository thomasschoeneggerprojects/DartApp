# Sta.DataSync

Reusable, offline-first synchronization for .NET 8 applications: local files and SQLite databases are kept in sync
across devices through a pluggable destination. The first destination is **Google Drive** (`appDataFolder`).

- Provider-independent engine: three-way change detection against a shared baseline, immutable plans, conflict
  strategies, journaled crash recovery, content-hash verification, bounded parallel streaming transfers.
- No silent overwrites: concurrent commits are detected (compare-and-swap where the destination supports it, a verified
  revision log where it does not, e.g. Google Drive), conflicts are kept until resolved, missing remote items are never
  treated as deletions.
- Works without a network: local data stays the source of truth, pending changes survive restarts, synchronization
  retries with backoff when the connection returns.

## Solution

```
Sta.DataSync.sln
├── src/
│   ├── Sta.DataSync.Abstractions           contracts, models, enums, events, exceptions (no dependencies)
│   ├── Sta.DataSync.Core                   engine: detection, manifest, planning, execution, conflicts, validation
│   ├── Sta.DataSync.Storage                SQLite state store, journal, schema migrations
│   ├── Sta.DataSync.Sources                file/directory source, SQLite snapshot source
│   ├── Sta.DataSync.Providers.GoogleDrive  Google Drive provider, OAuth (PKCE) for installed apps
│   ├── Sta.DataSync.Hosting                Microsoft DI registration, scheduler, options
│   └── Sta.DataSync.Maui                   MAUI adapters: sign-in, secure storage, connectivity, lifecycle
├── tests/                                  Core, Storage, GoogleDrive, Integration (xUnit)
├── samples/                                Console sample, MAUI sample (Android + Windows)
└── docs/                                   architecture, usage, Google setup, extending, operations
```

Dependencies point inward: `Core` depends only on `Abstractions`; `Core` knows nothing about Google Drive, MAUI,
SQLite or application models. All library projects target `net8.0` except `Sta.DataSync.Maui` (MAUI 9 targets,
matching the installed workloads; it only adapts platform APIs).

## Getting started

```csharp
builder.Services.AddDataSync(options =>
{
    options.ApplicationId = "DartTA";                 // isolates this app's data
    options.DefaultMode = SyncMode.Bidirectional;
    options.DefaultConflictStrategy = ConflictResolutionStrategy.KeepBoth;
});

builder.Services.AddGoogleDriveSyncProvider();
builder.Services.AddGoogleDesktopAuthentication(o => { o.ClientId = config["Google:ClientId"]!; o.ClientSecret = config["Google:ClientSecret"]; });
builder.Services.AddSingleton<IGoogleTokenStore>(new ProtectedFileTokenStore(tokenPath));   // Windows desktop
// MAUI instead: builder.Services.AddGoogleDriveMauiAuthentication(...); builder.UseDataSync();

builder.Services.AddFileSyncSource("settings", settingsDirectory, o => o.Include.Add("*.json"));
builder.Services.AddSqliteSnapshotSyncSource("database", databasePath);
builder.Services.AddDataSyncScheduler(o => o.Interval = TimeSpan.FromMinutes(15));
```

```csharp
SyncResult result = await dataSync.SynchronizeAsync(cancellationToken);           // all scopes
SyncResult one = await dataSync.SynchronizeAsync("settings", progress, cancellationToken);
IReadOnlyList<SyncConflict> open = await dataSync.GetConflictsAsync();
await dataSync.ResolveConflictAsync("settings", "theme.json", ConflictResolution.KeepRemote);
```

Build and test:

```bash
dotnet build Sta.DataSync.sln
dotnet test Sta.DataSync.sln
```

## Documentation

| Document | Content |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Components, synchronization pipeline, remote layout, concurrency protocol, recovery, design decisions |
| [docs/usage.md](docs/usage.md) | DI registration, data sources, modes and deletions, conflicts, scheduling, retries, results, API reference |
| [docs/google-drive-setup.md](docs/google-drive-setup.md) | Google Cloud project, OAuth clients for Windows / Android / iOS, samples, opt-in real-Drive tests |
| [docs/extending.md](docs/extending.md) | Custom providers, custom data sources, conflict resolvers, content transforms (future encryption), record-level sync |
| [docs/operations.md](docs/operations.md) | Security, platform limitations, logging and diagnostics, troubleshooting |

## Status of version 1

Implemented and covered by automated tests (fakes, the real SQLite engine, the official Google .NET client against an
in-memory Drive API): engine, state store, file and SQLite sources, Google Drive provider, PKCE authenticator,
scheduler, DI. Built for Android, iOS and Windows: the MAUI adapter. Not verified in this repository: sign-in and
synchronization against the real Google Drive service (an opt-in test exists but needs credentials), the Android
Google Identity Services flow and the iOS flow on devices. See [docs/operations.md](docs/operations.md#platform-limitations).
