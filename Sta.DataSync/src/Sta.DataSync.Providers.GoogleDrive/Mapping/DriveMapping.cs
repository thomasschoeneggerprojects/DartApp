using System.Globalization;
using System.Net;
using Google;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Sta.DataSync.Providers.GoogleDrive.Mapping;

/// <summary>Maps Drive metadata and errors to Sta.DataSync models. Defensive against incomplete remote metadata.</summary>
internal static class DriveMapping
{
    /// <summary>App property holding the scope prefix (root/application/scope) for server-side filtering.</summary>
    public const string ScopeProperty = "staScope";

    /// <summary>Fields requested for files (keeps responses small).</summary>
    public const string FileFields = "id, name, size, version, createdTime, modifiedTime, sha256Checksum";

    /// <summary>Drive limits key + value of an app property to 124 bytes.</summary>
    private const int MaxPropertyBytes = 124;

    public static RemoteSyncItem? ToRemoteItem(DriveFile file)
    {
        if (string.IsNullOrEmpty(file.Id) || string.IsNullOrEmpty(file.Name) || !SyncPath.IsValid(file.Name)) return null;
        return new RemoteSyncItem(
            file.Name,
            file.Id,
            (file.Version ?? 0).ToString(CultureInfo.InvariantCulture),
            file.Size ?? 0,
            string.IsNullOrEmpty(file.Sha256Checksum) ? null : ContentHash.Prefix + file.Sha256Checksum.ToLowerInvariant(),
            file.CreatedTimeDateTimeOffset,
            file.ModifiedTimeDateTimeOffset);
    }

    /// <summary>The first three path segments (root/application/scope), or <see langword="null"/> when shorter or too long for an app property.</summary>
    public static string? ScopeOf(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3) return null;
        string scope = string.Join('/', segments.Take(3));
        return System.Text.Encoding.UTF8.GetByteCount(ScopeProperty + scope) <= MaxPropertyBytes ? scope : null;
    }

    /// <summary>Escapes a value for a Drive query string literal.</summary>
    public static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>Maps a Drive error to the Sta.DataSync error model, keeping the original as inner exception.</summary>
    public static Exception Map(Exception exception, string operation) => exception switch
    {
        GoogleApiException api => MapApi(api, operation),
        HttpRequestException or IOException or TimeoutException => new SyncTransientException($"Google Drive {operation} failed: network error.", exception),
        TaskCanceledException => new SyncTransientException($"Google Drive {operation} timed out.", exception),
        _ => exception,
    };

    private static SyncException MapApi(GoogleApiException api, string operation)
    {
        string[] reasons = api.Error?.Errors?.Select(e => e.Reason ?? string.Empty).ToArray() ?? [];
        string message = $"Google Drive {operation} failed ({(int)api.HttpStatusCode} {string.Join(",", reasons)}).";
        return api.HttpStatusCode switch
        {
            HttpStatusCode.Unauthorized => new SyncAuthenticationException(message, api),
            HttpStatusCode.NotFound => new SyncItemNotFoundException(message, api),
            HttpStatusCode.TooManyRequests => new SyncTransientException(message, api),
            HttpStatusCode.PreconditionFailed => new SyncConcurrencyException(message, api),
            HttpStatusCode.Forbidden when reasons.Any(r => r is "rateLimitExceeded" or "userRateLimitExceeded" or "backendError")
                => new SyncTransientException(message, api),
            HttpStatusCode.Forbidden when reasons.Any(r => r is "insufficientPermissions" or "authError" or "insufficientScopes")
                => new SyncAuthenticationException(message, api),
            HttpStatusCode.RequestTimeout => new SyncTransientException(message, api),
            >= HttpStatusCode.InternalServerError => new SyncTransientException(message, api),
            _ => new SyncProviderException(message, api),
        };
    }
}
