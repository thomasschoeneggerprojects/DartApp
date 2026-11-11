# SportTA.Streaks

A reusable, UI-free and storage-agnostic streak system for .NET 8 apps and games: daily streaks, weekly
goals, consecutive win streaks and fully custom rules, with grace periods, freezes, restores, milestones and events.

The library contains no game-specific logic and never decides where data lives. The hosting application supplies
its data directory or its own persistence.

## Contents

| Path | What |
| --- | --- |
| `src/SportTA.Streaks` | The library (`net8.0`, nullable, trimming/AOT compatible). |
| `tests/SportTA.Streaks.Tests` | xUnit tests (deterministic fake clock and custom time zones). |
| `samples/SportTA.Streaks.Samples.ConsoleApp` | DI and manual integration, including a custom repository. |
| `build.ps1` | Restore, build, test, run sample. |
| `add-to-dartta.ps1` | Adds the library to the DartTA solution (temporary hosting). |
| `integration/DartTA` | DartTA-side wiring (`DartTaStreaks.cs`), copied into the app by the script. |

Dependencies: `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Logging.Abstractions`
(both 8.0.2). No UI framework, no reflection-based serialization (System.Text.Json source generation).

## Build and test

```powershell
./build.ps1
# or
dotnet build SportTA.Streaks.sln -c Release
dotnet test tests/SportTA.Streaks.Tests -c Release
```

The repository-level `nuget.config` restores from nuget.org only, so machine-wide private feeds don't break restore.
Test and sample projects use `RollForward=Major`, so they also run on machines that only have a newer runtime.

## Quick start (dependency injection)

```csharp
services.AddStreakSystem(options =>
{
    options.StorageDirectory = appDataDirectory;      // the directory your app already uses (absolute path)
    options.DefaultTimeZone = TimeZoneInfo.Local;     // your choice; the library defaults to UTC
    options.Definitions.Add(new StreakDefinition
    {
        Id = "daily-training",
        Name = "Daily Training",
        RequiredActivities = 1,
        Period = StreakPeriod.Daily,
        Milestones = [3, 7, 14, 30, 100],
    });
})
.AddEventHandler<MyAchievementHandler>();             // optional
```

```csharp
// On startup (or when opening the statistics screen): settle missed days/weeks.
await streakService.EvaluateAllAsync(playerId, cancellationToken);

// After an activity. A stable id makes retries and sync idempotent.
StreakResult result = await streakService.RecordActivityAsync(playerId, "daily-training", new StreakActivity
{
    Id = session.Id.ToString(),
    Timestamp = DateTimeOffset.UtcNow,
}, cancellationToken);

foreach (StreakEvent e in result.Events) { /* celebrate, unlock, notify */ }
```

`RecordActivityAsync(playerId, streakId, DateTimeOffset.UtcNow, ct)` also exists for one-off activities. It generates a
random id, so it is not idempotent.

Definitions can also be registered at runtime via `StreakRegistry.Register(...)` (resolve `StreakRegistry` from DI).

## Manual initialization (no DI)

```csharp
var storage = new FileStreakStorageProvider(appDataDirectory);
var repository = new JsonStreakRepository(storage);
var registry = new StreakRegistry([dailyTraining, weeklyWorkouts]);
var service = new StreakService(repository, registry, new StreakOptions { DefaultTimeZone = TimeZoneInfo.Local });
```

Optional constructor arguments: `TimeProvider` (clock), event handlers and an `ILogger<StreakService>`.

## Using the application's own storage

The host's storage location is always authoritative. There are three levels of replacement:

1. **Same JSON format, host directory** (default): set `StorageDirectory`. The library writes
   `streaks-{playerId}.json` (plus `.bak` and `.lock`) into exactly that directory and nothing else. Relative paths are
   rejected. `Environment.CurrentDirectory`, `AppContext.BaseDirectory` and special folders are never used.
2. **Same JSON format, different medium** (encrypted storage, save container, cloud blob): implement
   `IStreakStorageProvider` and call `.UseStorageProvider<T>()`.
3. **Fully custom persistence** (SQLite, existing repository, sync backend): implement `IStreakRepository` and either
   call `.UseRepository<T>()` or register it yourself:

```csharp
services.AddStreakSystem();
services.AddSingleton<IStreakRepository, ApplicationStreakRepository>(); // last registration wins
```

The default repository is registered with `TryAdd` and constructed lazily, so a replaced default is never created and
`StorageDirectory` isn't needed. `UseRepository<T>()` replaces the registration, leaving exactly one.

A custom repository must store every `Streak` property and implement optimistic concurrency: `SaveAsync(streak,
expectedVersion)` writes only if the stored version equals `expectedVersion` (0 = not stored) and otherwise throws
`StreakConcurrencyException`. See `samples/.../GameSaveStreakRepository.cs`.

## Concepts

**Periods.** `Daily` and `Weekly` streaks group activities into calendar periods in the player's time zone
(`DefaultTimeZone` or `PlayerTimeZoneResolver`). `PerActivity` streaks have no periods: every qualifying activity
counts, and failures can break the streak (e.g. win streaks).

**Requirements.** `RequiredActivities` per period. Several activities in one day never count the day twice. Weekly
streaks can count distinct days instead (`CountDistinctDays`, e.g. "exercise on 5 days per week").

**Rules.** `IStreakRule.Evaluate(activity, definition)` returns `Qualify`, `Fail` or `Ignore`. The default rule uses
`Categories`, `MinimumValue` and `ActivityOutcome`. Use `DelegateStreakRule` for small rules or implement
`IStreakRule` for complex ones. Rules must be pure and thread-safe.

**Grace periods.** `GracePeriods = n` tolerates n consecutive missed periods (or failures for per-activity streaks)
without breaking. Missed periods never increase the count. The allowance renews with each qualifying period.

**Freezes.** `AddFreezesAsync` grants freezes (capped by `MaxFreezes`). Freezes cover misses that grace periods don't,
and are consumed automatically. They are only spent if they save the streak.

**Restore.** After a break, `RestoreAsync` brings back the broken run (merging it with a run started since). The
application decides what restoring costs.

**Milestones.** Reached milestones are stored per run and for all time, so retries never reward twice.
`StreakEvent.IsFirstTimeMilestone` distinguishes first-time from repeat achievements.

**Evaluation is explicit.** There are no timers or background threads. `EvaluateAsync`/`EvaluateAllAsync` settle
periods that ended before now. `GetProgressAsync`/`GetAllAsync` show the evaluated state as of now without writing.

### Idempotency and late activities

- Each streak remembers the last `ProcessedActivityHistorySize` (default 256) activity ids. A repeated id returns
  `DuplicateActivity` with no change and no events.
- Activities for periods that are already settled return `LateActivity` (or `AlreadyCountedForPeriod`) and can't change
  the streak. Per-activity streaks also reject timestamps older than the last processed one. This guards retries that
  fall outside the id window.
- **Offline sync:** record pending activities oldest first, then call `EvaluateAllAsync`. Evaluating first can break a
  streak that the pending activities would have kept alive.
- Timestamps more than `MaxFutureTimestampSkew` (default 5 minutes) in the future are rejected.

### Time zones and DST

Periods are integer indices of local calendar days or weeks, so daylight-saving transitions (23- and 25-hour days)
never shift a boundary, and gaps of any length are computed arithmetically. If a player changes time zone westward,
the same local day isn't counted twice and the streak doesn't break. Moving eastward across midnight can make a
calendar day look skipped, the same as it would on a wall calendar.

### Events

Events (`Started`, `Increased`, `MilestoneReached`, `Broken`, `Restored`, `Frozen`, `Reset`) are returned in
`StreakResult.Events` and passed to registered `IStreakEventHandler`s. Both happen only after the change was persisted.
A failing handler is logged and affects neither the state nor other handlers. Duplicate activities produce no events.

### Concurrency

- Operations on the same player and streak are serialized in-process. The service holds no other mutable state and
  is registered as transient.
- The JSON repository runs every read-modify-write under an exclusive lock that combines an in-process async lock with a
  `.lock` file opened with `FileShare.None`. This also covers multiple service instances and multiple processes sharing
  the directory. Each write also checks the version.
- On a version conflict (e.g. two app instances, or a custom repository shared with a sync job) the service reloads and
  reapplies the operation up to `MaxConcurrencyRetries` times. After that, `StreakConcurrencyException` surfaces.

### Storage format and failure handling

- One JSON document per player: `{ "formatVersion": 1, "playerId": ..., "streaks": [...] }`.
- Writes go to a temporary file that atomically replaces the data file. The previous generation is kept as `.bak`.
- Corrupted data with a valid backup: the corrupted file is renamed to `*.corrupt-*` (never deleted) and the backup is
  restored.
- Corrupted data without a backup: `StreakDataCorruptedException`, and the file is left untouched. With
  `CorruptDataHandling.QuarantineAndReset` the file is quarantined instead and the player starts fresh.
- Data from a newer library version: `StreakDataVersionException`. Data is never downgraded.
- Older formats are migrated in memory and rewritten in the current format on the next save. To add format version N,
  bump `StreakJsonFormat.CurrentVersion` and add an `N-1 → N` step.

## Temporarily hosted inside DartTA

Until the library gets its own repository, it lives as one self-contained folder in the DartTA repo:

```powershell
# from the extracted SportTA.Streaks folder
.\add-to-dartta.ps1                       # defaults to C:\DartForge\DartApp
.\add-to-dartta.ps1 -DartRepo <path> -Solution <sln> -AppProject <csproj>   # if auto-detection is ambiguous
```

The script does the following:

1. Copies the folder to `<DartRepo>\SportTA.Streaks` with the same internal layout, including its standalone
   `.sln`, `nuget.config` and `build.ps1`.
2. Adds the library and its tests to the DartTA solution under the solution folder `SportTA.Streaks`.
3. Adds a `ProjectReference` from the DartTA app to the library.
4. If the app project sits above the folder, excludes the folder from the app's file globs.
5. Copies `integration/DartTA/Streaks/DartTaStreaks.cs` into the app project.
6. Runs the library tests.

The folder's own `Directory.Build.props` and `Directory.Packages.props` keep it independent of the DartTA repository's
build settings. All dart-specific code (`DartTaStreaks`, `DartStreakTracker`) lives in the app, not in the library.

To move it out later:

1. Remove the two projects from the DartTA solution.
2. Remove the `ProjectReference` and the `DefaultItemExcludes` line (if the script added one) from the app project.
3. Move the `SportTA.Streaks` folder to its own repository. It already has its own solution.
4. Reference it again as a project or NuGet package. The app-side `DartTaStreaks.cs` stays unchanged.

## Integration notes per app type

| Host | Storage directory | Notes |
| --- | --- | --- |
| MAUI (Android/iOS/Windows) | The folder the app already uses, typically `FileSystem.AppDataDirectory` | JSON uses source generation (trim/AOT safe). Can be referenced from `net8.0-*` or `net9.0-*` apps. |
| WPF | The app's existing settings/data folder | All I/O is async with `ConfigureAwait(false)`, so it never blocks the UI thread. |
| ASP.NET Core / Blazor | Usually a custom `IStreakRepository` on the app's database | Service is transient; scoped repositories (EF Core) are supported via `UseRepository<T>(ServiceLifetime.Scoped)`. |
| MonoGame | The game's save folder, or a custom provider on the save system | Call `EvaluateAllAsync` when loading a save. |
| Console / background services | Configured path | Call evaluation explicitly; no hosted service is included. |

### Example: DartTA (.NET MAUI Blazor)

The snippet below is the intended wiring for DartTA. It is **not part of this solution's build** because it needs the
MAUI workload. Check it against DartTA's `MauiProgram.cs` when integrating. Use the directory that already holds
`dartforge.db3`, so streak data lives next to the existing match history.

```csharp
// MauiProgram.cs
builder.Services.AddStreakSystem(options =>
{
    options.StorageDirectory = FileSystem.AppDataDirectory; // same folder as dartforge.db3
    options.DefaultTimeZone = TimeZoneInfo.Local;
    options.Definitions.Add(new StreakDefinition
    {
        Id = "daily-training",
        Name = "Daily Training",
        Period = StreakPeriod.Daily,
        Categories = ["training"],
        MinimumValue = 60,               // the home screen's 60-darts daily goal
        Milestones = [3, 7, 14, 30, 100],
    });
    options.Definitions.Add(new StreakDefinition
    {
        Id = "x01-win-streak",
        Name = "X01 win streak",
        Period = StreakPeriod.PerActivity,
        Categories = ["x01"],
        Milestones = [3, 5, 10],
    });
});
```

When a training session or match ends, record it with the session/match id as `StreakActivity.Id`, `Category`
`"training"` or `"x01"`, `Value` = darts thrown, and `Outcome` = win/loss. The display names stay in DartTA's resx files;
the library returns ids and numbers only.

## Mapping to the requested structure

The suggested layout was refined to avoid abstractions without a job:

| Suggested | Implemented as | Why |
| --- | --- | --- |
| `IStreakClock` | `System.TimeProvider` | Built into .NET 8; any `TimeProvider` registered by the host is used. |
| `StreakEvaluationService` | internal `Evaluation/StreakMutation` + `StreakPeriods` | Pure, I/O-free state machine; not a public service. |
| `DailyStreakRule`, `WeeklyStreakRule`, `ConsecutiveActivityRule` | `StreakDefinition.Period` + `DefaultStreakRule` | Period math belongs to the engine; rules only decide qualification. One configurable rule covers all three. |
| `CustomStreakRule` | `IStreakRule` + `DelegateStreakRule` | Apps implement the interface directly. |
| `StreakDefinition` registration | `StreakRegistry` (+ `StreakOptions.Definitions`) | Thread-safe and deterministic (registration order). |
| `GetProgressAsync` returns `StreakProgress?` | returns `StreakProgress` (status `NotStarted` when nothing is stored) | Simpler for UIs; unknown ids throw `StreakDefinitionNotFoundException`. |
| `ResetAsync` returns `Task` | returns `Task<StreakResult>` | Exposes the `Reset` event and the new progress. |

Additional API: `RecordActivityForAllAsync`, `EvaluateAllAsync`, `AddFreezesAsync`, `RestoreAsync`, `SetMetadataAsync`,
`DeleteAsync`.

## Test coverage

First activity, consecutive and same-day activities, missed days, grace periods, freezes, restore and reset, weekly
qualification (week start days, distinct days, incomplete weeks, year-spanning weeks), win/loss streaks, custom rules
(class and delegate), longest-streak tracking, milestones (once per run, repeats, across restarts), duplicate ids
(in-memory, after restart, beyond the id window, multi-streak), parallel activity processing (single instance, two
instances sharing a directory, conflict retries), restart and restore, JSON round-trip, version-0 migration, newer
versions, corrupted and empty files (with and without backup, quarantine mode), time zone changes, per-player time
zones, DST start and end, month, year and leap-year boundaries, cancellation (before start, waiting for a busy streak,
waiting for a file lock), persistence failures, failing event handlers, events-after-persistence ordering, argument
validation, and DI registration and replacement.
