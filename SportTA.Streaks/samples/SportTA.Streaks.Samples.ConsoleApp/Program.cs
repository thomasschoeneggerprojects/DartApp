using Microsoft.Extensions.DependencyInjection;
using SportTA.Streaks;
using SportTA.Streaks.Samples.ConsoleApp;

// ---------------------------------------------------------------------------------------------
// The HOST decides where data lives. In a real app this is the directory the app already uses
// (e.g. MAUI: FileSystem.AppDataDirectory, WPF: the app's existing settings folder).
// This sample takes it from the command line or uses a temp folder it chooses itself.
// ---------------------------------------------------------------------------------------------
string appDataDirectory = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(Path.GetTempPath(), "SportTA.Streaks.Sample");

Guid playerId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

Console.WriteLine("== 1. Dependency injection with the default JSON storage ==");
Console.WriteLine($"Host data directory: {appDataDirectory}");

var services = new ServiceCollection();
services.AddStreakSystem(options =>
    {
        options.StorageDirectory = appDataDirectory;
        options.DefaultTimeZone = TimeZoneInfo.Local; // the host's choice, never the library's
        options.Definitions.Add(StreakCatalog.DailyTraining);
        options.Definitions.Add(StreakCatalog.WeeklyWorkouts);
        options.Definitions.Add(StreakCatalog.X01WinStreak);
    })
    .AddEventHandler<ConsoleStreakEventHandler>();

await using (ServiceProvider provider = services.BuildServiceProvider())
{
    IStreakService streaks = provider.GetRequiredService<IStreakService>();

    // On startup: settle missed days/weeks (consumes grace/freezes or breaks streaks).
    await streaks.EvaluateAllAsync(playerId);

    // A training session finished: one activity, every streak decides via its rule.
    var session = new StreakActivity
    {
        Id = $"training-{DateTime.UtcNow:yyyyMMddHHmmss}", // stable id => safe to retry
        Timestamp = DateTimeOffset.UtcNow,
        Category = "training",
        Value = 60, // darts thrown
    };
    foreach (StreakResult result in await streaks.RecordActivityForAllAsync(playerId, session))
    {
        Console.WriteLine($"  {result.Progress.DefinitionId,-16} {result.Code,-24} count={result.Progress.CurrentCount}");
    }

    // A match was won.
    StreakResult win = await streaks.RecordActivityAsync(playerId, "x01-win-streak", new StreakActivity
    {
        Id = $"match-{Guid.NewGuid():N}",
        Timestamp = DateTimeOffset.UtcNow,
        Category = "x01",
        Outcome = ActivityOutcome.Success,
    });
    Console.WriteLine($"  x01 win streak: {win.Progress.CurrentCount} (longest {win.Progress.LongestCount})");

    // Statistics screen.
    foreach (StreakProgress progress in await streaks.GetAllAsync(playerId))
    {
        Console.WriteLine($"  [{progress.Name}] {progress.Status}, current {progress.CurrentCount}, longest {progress.LongestCount}, " +
                          $"this period {progress.ActivitiesInCurrentPeriod}/{progress.RequiredActivities}, at risk: {progress.IsAtRisk}");
    }
}

Console.WriteLine();
Console.WriteLine("== 2. Manual construction with the application's own save system ==");

var saveGame = new GameSaveStore();                          // the host's existing persistence
var repository = new GameSaveStreakRepository(saveGame);     // adapter implementing IStreakRepository
var registry = new StreakRegistry([StreakCatalog.DailyTraining]);
var service = new StreakService(repository, registry, new StreakOptions { DefaultTimeZone = TimeZoneInfo.Utc });

DateTimeOffset twoDaysAgo = DateTimeOffset.UtcNow.AddDays(-2);
for (int day = 0; day < 3; day++)
{
    StreakResult result = await service.RecordActivityAsync(playerId, "daily-training", new StreakActivity
    {
        Id = $"replay-{day}",
        Timestamp = twoDaysAgo.AddDays(day),
        Category = "training",
        Value = 60,
    });
    string events = string.Join(", ", result.Events.Select(e => e.Milestone is int m ? $"{e.Type}({m})" : e.Type.ToString()));
    Console.WriteLine($"  day {day + 1}: {result.Code} -> {result.Progress.CurrentCount} [{events}]");
}

Console.WriteLine($"  Save store now holds {saveGame.Count} streak record(s).");
