using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;
using SportTA.Streaks;

namespace DartForge.Streaks;

/// <summary>
/// DartTA's streak setup. All dart-specific choices (ids, goals, milestones) live here in the app,
/// never in the SportTA.Streaks library.
/// </summary>
public static class DartTaStreaks
{
    /// <summary>Daily goal reached (training darts today &gt;= <see cref="DailyGoalDarts"/>).</summary>
    public const string DailyGoal = "daily-goal";

    /// <summary>Trained on at least 3 different days in a week.</summary>
    public const string WeeklyTraining = "weekly-training";

    /// <summary>Consecutive won matches of the local player.</summary>
    public const string MatchWinStreak = "match-win-streak";

    /// <summary>Same value as the daily goal ring on the home screen.</summary>
    public const int DailyGoalDarts = 60;

    internal const string TrainingCategory = "training";
    internal const string DailyGoalCategory = "daily-goal";
    internal const string MatchCategory = "match";

    public static IReadOnlyList<StreakDefinition> Definitions { get; } =
    [
        new StreakDefinition
        {
            Id = DailyGoal,
            Period = StreakPeriod.Daily,
            Categories = [DailyGoalCategory],
            MaxFreezes = 2,
            Milestones = [3, 7, 14, 30, 100],
        },
        new StreakDefinition
        {
            Id = WeeklyTraining,
            Period = StreakPeriod.Weekly,
            WeekStartDay = DayOfWeek.Monday,
            RequiredActivities = 3,
            CountDistinctDays = true,
            Categories = [TrainingCategory],
            Milestones = [4, 12, 26, 52],
        },
        new StreakDefinition
        {
            Id = MatchWinStreak,
            Period = StreakPeriod.PerActivity,
            Categories = [MatchCategory],
            Milestones = [3, 5, 10],
        },
    ];

    /// <summary>
    /// Registers the streak system. Pass the directory that already holds <c>dartforge.db3</c>,
    /// so streak data lives next to the existing match history.
    /// </summary>
    public static IServiceCollection AddDartTaStreaks(this IServiceCollection services, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        services.AddStreakSystem(options =>
        {
            options.StorageDirectory = dataDirectory;
            options.DefaultTimeZone = TimeZoneInfo.Local;
            foreach (StreakDefinition definition in Definitions)
            {
                options.Definitions.Add(definition);
            }
        });

        services.AddSingleton(sp => new DartStreakTracker(
            sp.GetRequiredService<IStreakService>(),
            LocalStreakPlayer.GetOrCreateId()));

        return services;
    }
}

/// <summary>Stable id of the device's local player, kept in MAUI Preferences next to <c>dartta.language</c>.</summary>
internal static class LocalStreakPlayer
{
    private const string PreferenceKey = "dartta.streakPlayerId";

    public static Guid GetOrCreateId()
    {
        if (Guid.TryParse(Preferences.Default.Get(PreferenceKey, string.Empty), out Guid id) && id != Guid.Empty)
        {
            return id;
        }

        id = Guid.NewGuid();
        Preferences.Default.Set(PreferenceKey, id.ToString("D"));
        return id;
    }
}

/// <summary>
/// Thin DartTA facade over <see cref="IStreakService"/>. Every method is idempotent per session/match id,
/// so calling it again (e.g. after resuming a match) never counts twice.
/// </summary>
public sealed class DartStreakTracker(IStreakService streaks, Guid playerId)
{
    public Guid PlayerId => playerId;

    /// <summary>Call on app start and when opening the home or statistics screen.</summary>
    public Task<IReadOnlyList<StreakResult>> EvaluateAsync(CancellationToken cancellationToken = default)
        => streaks.EvaluateAllAsync(playerId, cancellationToken);

    /// <summary>Progress of all DartTA streaks for UI cards.</summary>
    public Task<IReadOnlyList<StreakProgress>> GetAllAsync(CancellationToken cancellationToken = default)
        => streaks.GetAllAsync(playerId, cancellationToken);

    /// <summary>
    /// Call when a training session ends.
    /// <paramref name="dartsToday"/> is the same total the home screen's daily goal ring shows (including this session).
    /// </summary>
    public async Task<IReadOnlyList<StreakEvent>> RecordTrainingAsync(
        string sessionId,
        DateTimeOffset finishedAt,
        int dartsThrown,
        int dartsToday,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var events = new List<StreakEvent>();

        StreakResult weekly = await streaks.RecordActivityAsync(playerId, DartTaStreaks.WeeklyTraining, new StreakActivity
        {
            Id = $"training-{sessionId}",
            Timestamp = finishedAt,
            Category = DartTaStreaks.TrainingCategory,
            Value = dartsThrown,
        }, cancellationToken);
        events.AddRange(weekly.Events);

        if (dartsToday >= DartTaStreaks.DailyGoalDarts)
        {
            // One activity per local day: the date-based id makes the daily goal count exactly once.
            string localDay = TimeZoneInfo.ConvertTime(finishedAt, TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            StreakResult daily = await streaks.RecordActivityAsync(playerId, DartTaStreaks.DailyGoal, new StreakActivity
            {
                Id = $"daily-goal-{localDay}",
                Timestamp = finishedAt,
                Category = DartTaStreaks.DailyGoalCategory,
            }, cancellationToken);
            events.AddRange(daily.Events);
        }

        return events;
    }

    /// <summary>Call when a match is finished (not for abandoned/unfinished matches).</summary>
    public async Task<IReadOnlyList<StreakEvent>> RecordMatchAsync(
        string matchId,
        DateTimeOffset finishedAt,
        bool localPlayerWon,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        StreakResult result = await streaks.RecordActivityAsync(playerId, DartTaStreaks.MatchWinStreak, new StreakActivity
        {
            Id = $"match-{matchId}",
            Timestamp = finishedAt,
            Category = DartTaStreaks.MatchCategory,
            Outcome = localPlayerWon ? ActivityOutcome.Success : ActivityOutcome.Failure,
        }, cancellationToken);
        return result.Events;
    }
}
