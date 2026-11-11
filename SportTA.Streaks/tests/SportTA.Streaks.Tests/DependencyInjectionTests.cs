using Microsoft.Extensions.DependencyInjection;
using SportTA.Streaks.Storage;
using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class DependencyInjectionTests
{
    private static readonly StreakDefinition Daily = new() { Id = "daily-training", Milestones = [3, 7] };
    private static readonly Guid Player = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task AddStreakSystem_UsesHostDirectory_AndRegisteredTimeProvider()
    {
        using var dir = new TempDirectory();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Utc(2026, 11, 2)));
        services.AddStreakSystem(options =>
        {
            options.StorageDirectory = dir.Path;
            options.Definitions.Add(Daily);
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        IStreakService service = provider.GetRequiredService<IStreakService>();
        StreakResult result = await service.RecordActivityAsync(Player, "daily-training", Utc(2026, 11, 2));

        Assert.Equal(StreakResultCode.Started, result.Code);
        Assert.True(File.Exists(dir.FileFor(Player)));
        Assert.IsType<JsonStreakRepository>(provider.GetRequiredService<IStreakRepository>());
    }

    [Fact]
    public void DefaultStorage_WithoutDirectory_FailsWithClearMessage()
    {
        var services = new ServiceCollection();
        services.AddStreakSystem();
        using ServiceProvider provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStreakService>());
        Assert.Contains(nameof(StreakOptions.StorageDirectory), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomRepository_RegisteredAfterwards_ReplacesDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Utc(2026, 11, 2)));
        services.AddStreakSystem(o => o.Definitions.Add(Daily));
        services.AddSingleton<IStreakRepository, InMemoryStreakRepository>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        var repository = Assert.IsType<InMemoryStreakRepository>(provider.GetRequiredService<IStreakRepository>());
        await provider.GetRequiredService<IStreakService>().RecordActivityAsync(Player, "daily-training", Utc(2026, 11, 2));

        Assert.Equal(1, repository.Count);
    }

    [Fact]
    public void UseRepository_LeavesExactlyOneRegistration()
    {
        var services = new ServiceCollection();
        services.AddStreakSystem().UseRepository<InMemoryStreakRepository>();

        Assert.Single(services, d => d.ServiceType == typeof(IStreakRepository));
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<InMemoryStreakRepository>(provider.GetRequiredService<IStreakRepository>());
    }

    [Fact]
    public async Task EventHandlers_AddedThroughBuilder_ReceiveEvents()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Utc(2026, 11, 2)));
        services.AddSingleton<EventSink>();
        services.AddStreakSystem(o => o.Definitions.Add(Daily))
            .UseRepository<InMemoryStreakRepository>()
            .AddEventHandler<SinkHandler>()
            .AddEventHandler<SinkHandler>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IStreakService>().RecordActivityAsync(Player, "daily-training", Utc(2026, 11, 2));

        Assert.Equal(StreakEventType.Started, Assert.Single(provider.GetRequiredService<EventSink>().Events).Type);
    }

    [Fact]
    public void AddStreakSystem_Twice_Throws()
    {
        var services = new ServiceCollection();
        services.AddStreakSystem();

        Assert.Throws<InvalidOperationException>(() => services.AddStreakSystem());
    }

    [Fact]
    public void RelativeStorageDirectory_IsRejectedAtRegistration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddStreakSystem(o => o.StorageDirectory = "data"));
    }

    [Fact]
    public void Registry_ContainsConfiguredDefinitions()
    {
        var services = new ServiceCollection();
        services.AddStreakSystem(o => o.Definitions.Add(Daily));
        using ServiceProvider provider = services.BuildServiceProvider();

        StreakRegistry registry = provider.GetRequiredService<StreakRegistry>();
        Assert.True(registry.TryGet("daily-training", out StreakDefinition? definition));
        Assert.Equal(new[] { 3, 7 }, definition!.Milestones);
    }

    public sealed class EventSink
    {
        public List<StreakEvent> Events { get; } = [];
    }

    public sealed class SinkHandler(EventSink sink) : IStreakEventHandler
    {
        public Task HandleAsync(StreakEvent streakEvent, CancellationToken cancellationToken)
        {
            sink.Events.Add(streakEvent);
            return Task.CompletedTask;
        }
    }
}
