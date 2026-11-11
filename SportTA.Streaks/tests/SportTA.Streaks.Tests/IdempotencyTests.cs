using SportTA.Streaks.Storage;
using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class IdempotencyTests
{
    private static readonly StreakDefinition Daily = new() { Id = "daily" };
    private static readonly StreakDefinition Wins = new() { Id = "wins", Period = StreakPeriod.PerActivity };

    [Fact]
    public async Task SameActivityId_IsProcessedOnce()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [Daily]);
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily", "workout-1");

        StreakResult retry = await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily", "workout-1");
        StreakResult retryNextDay = await ctx.RecordAtAsync(Utc(2026, 11, 3), "daily", "workout-1");

        Assert.Equal(StreakResultCode.DuplicateActivity, retry.Code);
        Assert.False(retry.StateChanged);
        Assert.Empty(retry.Events);
        Assert.Equal(StreakResultCode.DuplicateActivity, retryNextDay.Code);
        Assert.Equal(1, retryNextDay.Progress.CurrentCount);
        Assert.Equal(1, retryNextDay.Progress.TotalQualifyingActivities);
        Assert.Single(ctx.Handler.Events);
    }

    [Fact]
    public async Task Duplicate_IsDetectedAfterRestart()
    {
        using var dir = new TempDirectory();
        var first = new StreakTestContext(Utc(2026, 11, 2), [Wins], repository: new JsonStreakRepository(new FileStreakStorageProvider(dir.Path)));
        await first.RecordAtAsync(Utc(2026, 11, 2), "wins", "match-42");

        var second = new StreakTestContext(Utc(2026, 11, 2), [Wins], repository: new JsonStreakRepository(new FileStreakStorageProvider(dir.Path)));
        StreakResult retry = await second.RecordAtAsync(Utc(2026, 11, 2), "wins", "match-42");

        Assert.Equal(StreakResultCode.DuplicateActivity, retry.Code);
        Assert.Equal(1, retry.Progress.CurrentCount);
    }

    [Fact]
    public async Task RetryOutsideIdWindow_IsStillNotCounted()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [Wins], o => o.ProcessedActivityHistorySize = 2);
        await ctx.RecordAtAsync(Utc(2026, 11, 2, 10), "wins", "a");
        await ctx.RecordAtAsync(Utc(2026, 11, 2, 11), "wins", "b");
        await ctx.RecordAtAsync(Utc(2026, 11, 2, 12), "wins", "c");

        StreakResult retry = await ctx.Service.RecordActivityAsync(ctx.PlayerId, "wins", new StreakActivity { Id = "a", Timestamp = Utc(2026, 11, 2, 10) });

        Assert.Equal(StreakResultCode.LateActivity, retry.Code);
        Assert.Equal(3, retry.Progress.CurrentCount);
        Assert.Equal(3, retry.Progress.TotalQualifyingActivities);
    }

    [Fact]
    public async Task RecordForAll_IsIdempotentPerStreak()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [Daily, Wins]);
        var activity = new StreakActivity { Id = "session-7", Timestamp = Utc(2026, 11, 2) };

        IReadOnlyList<StreakResult> first = await ctx.Service.RecordActivityForAllAsync(ctx.PlayerId, activity);
        IReadOnlyList<StreakResult> second = await ctx.Service.RecordActivityForAllAsync(ctx.PlayerId, activity);

        Assert.All(first, r => Assert.Equal(StreakResultCode.Started, r.Code));
        Assert.All(second, r => Assert.Equal(StreakResultCode.DuplicateActivity, r.Code));
        Assert.Equal(new[] { "daily", "wins" }, first.Select(r => r.Progress.DefinitionId));
    }

    [Fact]
    public async Task FutureTimestamp_IsRejected_AndNothingIsStored()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2, 12), [Daily]);

        StreakResult result = await ctx.Service.RecordActivityAsync(ctx.PlayerId, "daily", new StreakActivity { Id = "x", Timestamp = Utc(2026, 11, 2, 13) });
        StreakResult withinSkew = await ctx.Service.RecordActivityAsync(ctx.PlayerId, "daily", new StreakActivity { Id = "y", Timestamp = Utc(2026, 11, 2, 12, 4) });

        Assert.Equal(StreakResultCode.RejectedFutureTimestamp, result.Code);
        Assert.False(result.StateChanged);
        Assert.Equal(StreakResultCode.Started, withinSkew.Code);
    }

    [Fact]
    public async Task ConvenienceOverload_CreatesUniqueActivities()
    {
        var ctx = new StreakTestContext(Utc(2026, 11, 2, 12), [Wins]);

        await ctx.Service.RecordActivityAsync(ctx.PlayerId, "wins", Utc(2026, 11, 2, 12));
        StreakResult second = await ctx.Service.RecordActivityAsync(ctx.PlayerId, "wins", Utc(2026, 11, 2, 12));

        Assert.Equal(2, second.Progress.CurrentCount);
    }
}
