using Microsoft.Extensions.Options;

namespace Sta.DataSync.Core.Tests.Fakes;

/// <summary>A simulated device: its own data source, state store and service, sharing the destination.</summary>
internal sealed class Device : IDisposable
{
    public Device(
        FakeProvider provider,
        SyncMode mode = SyncMode.Bidirectional,
        ConflictResolutionStrategy strategy = ConflictResolutionStrategy.KeepBoth,
        Action<DataSyncOptions>? configure = null,
        IConflictResolver? resolver = null,
        FakeSource? source = null,
        string scopeId = "data")
    {
        Provider = provider;
        Source = source ?? new FakeSource(scopeId);
        var options = new DataSyncOptions
        {
            ApplicationId = "TestApp",
            CommitVerificationDelay = TimeSpan.Zero,
            Retry = new SyncRetryOptions { MaxAttempts = 3, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero },
        };
        configure?.Invoke(options);
        Service = new DataSyncService(
            [new SyncScopeDefinition(scopeId, Source, provider) { Mode = mode, ConflictStrategy = strategy }],
            State,
            Options.Create(options),
            resolver is null ? null : [resolver]);
        ScopeId = scopeId;
    }

    public FakeProvider Provider { get; }

    public FakeSource Source { get; }

    public FakeStateStore State { get; } = new();

    public DataSyncService Service { get; }

    public string ScopeId { get; }

    public Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default) => Service.SynchronizeAsync(ScopeId, cancellationToken);

    public void Dispose() => Service.Dispose();
}
