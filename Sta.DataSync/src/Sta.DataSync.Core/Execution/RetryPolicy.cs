using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Sta.DataSync.Execution;

/// <summary>
/// Retries transient failures with exponential backoff and full jitter, honoring server <c>Retry-After</c> hints.
/// Authentication, concurrency, integrity and other permanent errors are never retried here: concurrency failures are
/// re-planned by the engine, credentials need the user.
/// </summary>
internal sealed partial class RetryPolicy(SyncRetryOptions options, TimeProvider time, ILogger logger)
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < options.MaxAttempts && IsTransient(ex, cancellationToken))
            {
                TimeSpan delay = DelayFor(attempt, (ex as SyncTransientException)?.RetryAfter);
                LogRetry(logger, attempt, options.MaxAttempts, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        => ExecuteAsync<bool>(async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>Transient: <see cref="SyncTransientException"/>, raw network errors and timeouts not caused by the caller.</summary>
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        SyncException sync => sync.IsTransient,
        HttpRequestException => true,
        TimeoutException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    internal TimeSpan DelayFor(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } requested) return requested < options.MaxDelay ? requested : options.MaxDelay;

        double exponential = options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        double capped = Math.Min(exponential, options.MaxDelay.TotalMilliseconds);
        // Full jitter keeps devices that failed together from retrying in lockstep.
        double jittered = capped * (0.5 + (RandomNumberGenerator.GetInt32(0, 1001) / 2000.0));
        return TimeSpan.FromMilliseconds(jittered);
    }

    [LoggerMessage(EventId = 1030, Level = LogLevel.Warning, Message = "Transient failure (attempt {Attempt} of {MaxAttempts}); retrying in {DelaySeconds:0.0}s: {Reason}")]
    private static partial void LogRetry(ILogger logger, int attempt, int maxAttempts, double delaySeconds, string reason);
}
