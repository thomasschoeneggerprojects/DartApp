using SportTA.Streaks.Storage;
using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class ConcurrencyTests
{
    private static readonly StreakDefinition Wins = new() { Id = "wins", Period = StreakPeriod.PerActivity };
    private static readonly DateTimeOffset Now = Utc(2026, 11, 2, 12);

    private static StreakActivity Activity(string id) => new() { Id = id, Timestamp = Now };

    [Fact]
    public async Task ParallelActivities_AreAllCounted_WithJsonStorage()
    {
        using var dir = new TempDirectory();
        var ctx = new StreakTestContext(Now, [Wins], repository: new JsonStreakRepository(new FileStreakStorageProvider(dir.Path)));

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => ctx.Service.RecordActivityAsync(ctx.PlayerId, "wins", Activity($"a{i}")))));

        StreakProgress progress = await ctx.Service.GetProgressAsync(ctx.PlayerId, "wins");
        Assert.Equal(40, progress.CurrentCount);
        Assert.Equal(40, progress.TotalQualifyingActivities);
    }

    [Fact]
    public async Task ParallelRetriesOfSameActivity_CountOnce_AndNotifyOnce()
    {
        var ctx = new StreakTestContext(Now, [Wins]);

        StreakResult[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => ctx.Service.RecordActivityAsync(ctx.PlayerId, "wins", Activity("same")))));

        Assert.Single(results, r => r.Code == StreakResultCode.Started);
        Assert.Equal(19, results.Count(r => r.Code == StreakResultCode.DuplicateActivity));
        Assert.Single(ctx.Handler.Events);
    }

    [Fact]
    public async Task TwoServiceInstances_SharingADirectory_DoNotLoseUpdates()
    {
        using var dir = new TempDirectory();
        var a = new StreakTestContext(Now, [Wins], repository: new JsonStreakRepository(new FileStreakStorageProvider(dir.Path)));
        var b = new StreakTestContext(Now, [Wins], repository: new JsonStreakRepository(new FileStreakStorageProvider(dir.Path)));

        IEnumerable<Task<StreakResult>> fromA = Enumerable.Range(0, 20).Select(i => Task.Run(() => a.Service.RecordActivityAsync(a.PlayerId, "wins", Activity($"a{i}"))));
        IEnumerable<Task<StreakResult>> fromB = Enumerable.Range(0, 20).Select(i => Task.Run(() => b.Service.RecordActivityAsync(b.PlayerId, "wins", Activity($"b{i}"))));
        await Task.WhenAll(fromA.Concat(fromB));

        Assert.Equal(40, (await a.Service.GetProgressAsync(a.PlayerId, "wins")).CurrentCount);
    }

    [Fact]
    public async Task ConcurrencyConflict_IsRetried()
    {
        var inner = new InMemoryStreakRepository();
        var ctx = new StreakTestContext(Now, [Wins], repository: new ConflictingRepository(inner, failures: 2));

        StreakResult result = await ctx.Service.RecordActivityAsync(ctx.PlayerId, "wins", Activity("x"));

        Assert.Equal(StreakResultCode.Started, result.Code);
        Assert.Equal(1, inner.Count);
        Assert.Single(ctx.Handler.Events);
    }

    [Fact]
    public async Task PersistentConflict_Surfaces_WithoutEvents()
    {
        var ctx = new StreakTestContext(
            Now,
            [Wins],
            o => o.MaxConcurrencyRetries = 1,
            repository: new ConflictingRepository(new InMemoryStreakRepository(), failures: 10));

        await Assert.ThrowsAsync<StreakConcurrencyException>(() => ctx.Service.RecordActivityAsync(ctx.PlayerId, "wins", Activity("x")));
        Assert.Empty(ctx.Handler.Events);
    }

    [Fact]
    public async Task Repository_RejectsStaleVersion()
    {
        using var dir = new TempDirectory();
        var repository = new JsonStreakRepository(new FileStreakStorageProvider(dir.Path));
        var playerId = Guid.NewGuid();
        Streak streak = new()
        {
            Id = Guid.NewGuid(),
            DefinitionId = "wins",
            PlayerId = playerId,
            CreatedAt = Now,
            UpdatedAt = Now,
            Version = 1,
        };

        await repository.SaveAsync(streak, expectedVersion: 0);

        await Assert.ThrowsAsync<StreakConcurrencyException>(() => repository.SaveAsync(streak with { Version = 2 }, expectedVersion: 0));
        await repository.SaveAsync(streak with { Version = 2 }, expectedVersion: 1);
        Assert.Equal(2, (await repository.GetAsync(playerId, "wins"))!.Version);
    }
}
