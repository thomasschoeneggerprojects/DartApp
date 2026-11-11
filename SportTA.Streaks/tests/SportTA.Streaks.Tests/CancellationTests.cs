using SportTA.Streaks.Storage;
using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class CancellationTests
{
    private static readonly StreakDefinition Daily = new() { Id = "daily" };
    private static readonly DateTimeOffset Now = Utc(2026, 11, 2);

    [Fact]
    public async Task CanceledToken_ThrowsBeforeAnyChange()
    {
        var ctx = new StreakTestContext(Now, [Daily]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.Service.RecordActivityAsync(ctx.PlayerId, "daily", Now, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.Service.GetProgressAsync(ctx.PlayerId, "daily", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.Service.EvaluateAllAsync(ctx.PlayerId, cts.Token));
        Assert.Equal(0, ((InMemoryStreakRepository)ctx.Repository).Count);
    }

    [Fact]
    public async Task WaitingForBusyStreak_CanBeCanceled()
    {
        var repository = new BlockingRepository(new InMemoryStreakRepository());
        var ctx = new StreakTestContext(Now, [Daily], repository: repository);

        Task<StreakResult> first = ctx.Service.RecordActivityAsync(ctx.PlayerId, "daily", Now);
        await repository.FirstReadStarted;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.Service.RecordActivityAsync(ctx.PlayerId, "daily", Now, cts.Token));

        repository.Release();
        Assert.Equal(StreakResultCode.Started, (await first).Code);
    }

    [Fact]
    public async Task WaitingForFileLockHeldByAnotherInstance_CanBeCanceled()
    {
        using var dir = new TempDirectory();
        var holder = new FileStreakStorageProvider(dir.Path);
        var waiter = new FileStreakStorageProvider(dir.Path, TimeSpan.FromSeconds(30));

        IAsyncDisposable held = await holder.AcquireLockAsync("streaks-test");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.AcquireLockAsync("streaks-test", cts.Token));
        }
        finally
        {
            await held.DisposeAsync();
        }

        IAsyncDisposable acquired = await waiter.AcquireLockAsync("streaks-test");
        await acquired.DisposeAsync();
    }
}
