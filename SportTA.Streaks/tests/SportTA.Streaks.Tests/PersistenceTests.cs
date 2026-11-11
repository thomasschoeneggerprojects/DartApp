using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using SportTA.Streaks.Storage;
using SportTA.Streaks.Tests.Infrastructure;
using static SportTA.Streaks.Tests.Infrastructure.StreakTestContext;

namespace SportTA.Streaks.Tests;

public sealed class PersistenceTests : IDisposable
{
    private static readonly StreakDefinition Daily = new() { Id = "daily", Milestones = [3] };
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private StreakTestContext CreateContext(DateTimeOffset now, CorruptDataHandling handling = CorruptDataHandling.Throw)
        => new(now, [Daily], repository: new JsonStreakRepository(new FileStreakStorageProvider(_dir.Path), handling));

    [Fact]
    public async Task StateSurvivesRestart()
    {
        var first = CreateContext(Utc(2026, 11, 2));
        foreach (int day in new[] { 2, 3, 4 })
        {
            await first.RecordAtAsync(Utc(2026, 11, day), "daily");
        }

        var second = CreateContext(Utc(2026, 11, 4, 20));
        StreakProgress progress = await second.Service.GetProgressAsync(second.PlayerId, "daily");

        Assert.Equal(3, progress.CurrentCount);
        Assert.Equal(new[] { 3 }, progress.ReachedMilestones);
    }

    [Fact]
    public async Task Data_IsStoredOnlyInHostDirectory()
    {
        var ctx = CreateContext(Utc(2026, 11, 2));
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "daily");

        Assert.True(File.Exists(_dir.FileFor(ctx.PlayerId)));
        Assert.Empty(Directory.GetDirectories(_dir.Path));
        Assert.All(Directory.GetFiles(_dir.Path), f => Assert.StartsWith("streaks-", Path.GetFileName(f), StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.GetFiles(_dir.Path), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void RelativeStorageDirectory_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new FileStreakStorageProvider("streak-data"));
        Assert.Throws<ArgumentException>(() => new StreakOptions { StorageDirectory = "streak-data" }.Validate());
    }

    [Fact]
    public async Task CorruptedFile_WithoutBackup_Throws_AndIsLeftUntouched()
    {
        var ctx = CreateContext(Utc(2026, 11, 2));
        string path = _dir.FileFor(ctx.PlayerId);
        await File.WriteAllTextAsync(path, "{ this is not json");

        var ex = await Assert.ThrowsAsync<StreakDataCorruptedException>(() => ctx.Service.GetProgressAsync(ctx.PlayerId, "daily"));
        await Assert.ThrowsAsync<StreakDataCorruptedException>(() => ctx.RecordAtAsync(Utc(2026, 11, 2), "daily"));

        Assert.Equal(JsonStreakRepository.GetStorageKey(ctx.PlayerId), ex.StorageKey);
        Assert.Equal("{ this is not json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EmptyFile_IsTreatedAsCorrupted()
    {
        var ctx = CreateContext(Utc(2026, 11, 2));
        await File.WriteAllBytesAsync(_dir.FileFor(ctx.PlayerId), []);

        await Assert.ThrowsAsync<StreakDataCorruptedException>(() => ctx.Service.GetProgressAsync(ctx.PlayerId, "daily"));
    }

    [Fact]
    public async Task CorruptedFile_IsRecoveredFromBackup_AndQuarantined()
    {
        var ctx = CreateContext(Utc(2026, 11, 2));
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");
        await ctx.RecordAtAsync(Utc(2026, 11, 3), "daily");
        string path = _dir.FileFor(ctx.PlayerId);
        Assert.True(File.Exists(path + ".bak"));
        await File.WriteAllTextAsync(path, "garbage");

        var restarted = CreateContext(Utc(2026, 11, 3, 20));
        StreakProgress progress = await restarted.Service.GetProgressAsync(restarted.PlayerId, "daily");

        Assert.Equal(1, progress.CurrentCount);
        Assert.Single(Directory.GetFiles(_dir.Path, "*.corrupt-*"));
        Assert.Equal(1, (await restarted.Service.GetProgressAsync(restarted.PlayerId, "daily")).CurrentCount);
        StreakResult next = await restarted.RecordAtAsync(Utc(2026, 11, 3, 21), "daily");
        Assert.Equal(2, next.Progress.CurrentCount);
    }

    [Fact]
    public async Task CorruptedFile_QuarantineAndReset_StartsFresh_ButKeepsTheFile()
    {
        var ctx = CreateContext(Utc(2026, 11, 2), CorruptDataHandling.QuarantineAndReset);
        await File.WriteAllTextAsync(_dir.FileFor(ctx.PlayerId), "garbage");

        StreakResult result = await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");

        Assert.Equal(StreakResultCode.Started, result.Code);
        string quarantined = Assert.Single(Directory.GetFiles(_dir.Path, "*.corrupt-*"));
        Assert.Equal("garbage", await File.ReadAllTextAsync(quarantined));
    }

    [Fact]
    public async Task NewerFormatVersion_IsRejected_AndNotModified()
    {
        var ctx = CreateContext(Utc(2026, 11, 2));
        string path = _dir.FileFor(ctx.PlayerId);
        string json = $$"""{"formatVersion":99,"playerId":"{{ctx.PlayerId}}","streaks":[]}""";
        await File.WriteAllTextAsync(path, json);

        var ex = await Assert.ThrowsAsync<StreakDataVersionException>(() => ctx.RecordAtAsync(Utc(2026, 11, 2), "daily"));

        Assert.Equal(99, ex.FoundVersion);
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LegacyVersion0_IsMigrated_AndRewrittenAsCurrentVersion()
    {
        var ctx = CreateContext(Utc(2026, 11, 2));
        await ctx.RecordAtAsync(Utc(2026, 11, 2), "daily");
        string path = _dir.FileFor(ctx.PlayerId);

        // Rewrite the file in the legacy layout: a bare array of records.
        JsonNode current = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        JsonNode legacy = current["streaks"]!.DeepClone();
        await File.WriteAllTextAsync(path, legacy.ToJsonString());

        var restarted = CreateContext(Utc(2026, 11, 3));
        Assert.Equal(1, (await restarted.Service.GetProgressAsync(restarted.PlayerId, "daily")).CurrentCount);
        await restarted.RecordAtAsync(Utc(2026, 11, 3), "daily");

        JsonNode rewritten = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal(1, rewritten["formatVersion"]!.GetValue<int>());
        Assert.Equal(2, (await restarted.Service.GetProgressAsync(restarted.PlayerId, "daily")).CurrentCount);
    }

    [Fact]
    public async Task Serialization_RoundTripsEveryField()
    {
        var repository = new JsonStreakRepository(new FileStreakStorageProvider(_dir.Path));
        var playerId = Guid.NewGuid();
        var t = new DateTimeOffset(2026, 11, 2, 10, 30, 15, 123, TimeSpan.Zero);
        var original = new Streak
        {
            Id = Guid.NewGuid(),
            DefinitionId = "daily",
            PlayerId = playerId,
            Status = StreakStatus.InGrace,
            CurrentCount = 4,
            LongestCount = 9,
            LongestCountAchievedAt = t.AddDays(-10),
            TotalQualifyingActivities = 42,
            TotalQualifiedPeriods = 17,
            TimesBroken = 2,
            CurrentStreakStartedAt = t.AddDays(-4),
            FirstActivityAt = t.AddDays(-30),
            LastQualifyingActivityAt = t.AddDays(-1),
            LastProcessedActivityAt = t,
            LastQualifiedPeriod = 739900,
            SettledThroughPeriod = 739901,
            OpenPeriod = 739902,
            OpenPeriodActivityCount = 1,
            OpenPeriodActiveDays = [739902],
            ConsecutiveMissedPeriods = 1,
            FreezesAvailable = 3,
            FreezesUsed = 5,
            RestorableCount = 6,
            RestorableStartedAt = t.AddDays(-20),
            RestorableMilestones = [3],
            BrokenAt = t.AddDays(-5),
            CurrentRunMilestones = [3],
            AllTimeMilestones = [3, 7],
            RecentActivityIds = ["a", "b"],
            Metadata = ImmutableDictionary<string, string>.Empty.Add("k", "v"),
            CreatedAt = t.AddDays(-30),
            UpdatedAt = t,
            Version = 12,
        };

        await repository.SaveAsync(original, expectedVersion: 0);
        Streak loaded = (await repository.GetAsync(playerId, "daily"))!;

        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(loaded));
    }

    [Fact]
    public async Task StorageDirectoryThatIsAFile_FailsWithStorageException_AndRaisesNoEvents()
    {
        string blocker = Path.Combine(_dir.Path, "blocker");
        await File.WriteAllTextAsync(blocker, "x");
        var ctx = new StreakTestContext(Utc(2026, 11, 2), [Daily], repository: new JsonStreakRepository(new FileStreakStorageProvider(blocker)));

        await Assert.ThrowsAsync<StreakStorageException>(() => ctx.RecordAtAsync(Utc(2026, 11, 2), "daily"));
        Assert.Empty(ctx.Handler.Events);
    }
}
