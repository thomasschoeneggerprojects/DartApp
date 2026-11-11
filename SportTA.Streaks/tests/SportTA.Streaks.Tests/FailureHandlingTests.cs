using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class FailureHandlingTests
{
    private static readonly StreakDefinition Daily = new() { Id = "daily", Milestones = [1] };
    private static readonly DateTimeOffset Now = Utc(2026, 11, 2);

    [Fact]
    public async Task PersistenceFailure_Propagates_AndRaisesNoEvents()
    {
        var inner = new InMemoryStreakRepository();
        var ctx = new StreakTestContext(Now, [Daily], repository: new FailingRepository(inner));

        await Assert.ThrowsAsync<IOException>(() => ctx.RecordAtAsync(Now, "daily"));

        Assert.Empty(ctx.Handler.Events);
        Assert.Equal(0, inner.Count);
    }

    [Fact]
    public async Task FailingEventHandler_DoesNotAffectStateOrOtherHandlers()
    {
        var throwing = new ThrowingEventHandler();
        var ctx = new StreakTestContext(Now, [Daily], extraHandlers: [throwing]);

        StreakResult result = await ctx.RecordAtAsync(Now, "daily", "a1");
        StreakResult retry = await ctx.RecordAtAsync(Now, "daily", "a1");

        Assert.Equal(StreakResultCode.Started, result.Code);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(2, throwing.Calls);
        Assert.Equal(2, ctx.Handler.Events.Count);
        Assert.Equal(1, (await ctx.Service.GetProgressAsync(ctx.PlayerId, "daily")).CurrentCount);
        Assert.Equal(StreakResultCode.DuplicateActivity, retry.Code);
        Assert.Equal(2, throwing.Calls);
    }

    [Fact]
    public async Task Events_AreRaisedOnlyAfterPersistence()
    {
        var repository = new InMemoryStreakRepository();
        var observer = new PersistenceObservingHandler(repository);
        var ctx = new StreakTestContext(Now, [Daily], repository: repository, extraHandlers: [observer]);

        await ctx.RecordAtAsync(Now, "daily");

        Assert.NotEmpty(observer.StoredCountsAtNotification);
        Assert.All(observer.StoredCountsAtNotification, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task UnknownDefinition_Throws()
    {
        var ctx = new StreakTestContext(Now, [Daily]);

        await Assert.ThrowsAsync<StreakDefinitionNotFoundException>(() => ctx.Service.RecordActivityAsync(ctx.PlayerId, "nope", Now));
    }

    [Fact]
    public async Task InvalidArguments_AreRejected()
    {
        var ctx = new StreakTestContext(Now, [Daily]);

        await Assert.ThrowsAsync<ArgumentException>(() => ctx.Service.RecordActivityAsync(Guid.Empty, "daily", Now));
        await Assert.ThrowsAsync<ArgumentException>(() => ctx.Service.RecordActivityAsync(ctx.PlayerId, " ", Now));
        await Assert.ThrowsAsync<ArgumentException>(() => ctx.Service.RecordActivityAsync(ctx.PlayerId, "daily", new StreakActivity { Id = "", Timestamp = Now }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ctx.Service.RecordActivityAsync(ctx.PlayerId, "daily", (StreakActivity)null!));
    }

    [Fact]
    public void DuplicateDefinitionIds_AreRejected()
    {
        var registry = new StreakRegistry();
        registry.Register(Daily);

        Assert.Throws<InvalidOperationException>(() => registry.Register(Daily with { Name = "Other" }));
    }

    private sealed class PersistenceObservingHandler(IStreakRepository repository) : IStreakEventHandler
    {
        public List<int> StoredCountsAtNotification { get; } = [];

        public async Task HandleAsync(StreakEvent streakEvent, CancellationToken cancellationToken)
        {
            Streak? stored = await repository.GetAsync(streakEvent.PlayerId, streakEvent.DefinitionId, cancellationToken);
            StoredCountsAtNotification.Add(stored?.CurrentCount ?? -1);
        }
    }
}
