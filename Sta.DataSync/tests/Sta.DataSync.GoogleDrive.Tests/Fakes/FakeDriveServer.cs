using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Google.Apis.Http;
using Sta.DataSync.Providers.GoogleDrive.Authentication;

namespace Sta.DataSync.Testing;

/// <summary>
/// In-memory implementation of the Google Drive v3 REST endpoints used by the provider (files list/get media/delete,
/// resumable create/update uploads, change tokens). Lets tests run the official Google client unchanged.
/// </summary>
internal sealed partial class FakeDriveServer : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DriveEntry> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? FileId, JsonObject Metadata)> _sessions = new(StringComparer.Ordinal);
    private readonly List<(long Sequence, string FileId, string Name, bool Removed)> _changes = [];
    private long _sequence;
    private long _clock;
    private int _ids;

    /// <summary>Items per list page (small to exercise pagination).</summary>
    public int PageSize { get; set; } = 3;

    /// <summary>Returns a response to short-circuit a request (fault injection), or <see langword="null"/>.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    public ConcurrentBag<string> Requests { get; } = [];

    public ConcurrentBag<string?> AuthorizationHeaders { get; } = [];

    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate) return _files.Values.Select(f => f.Name).Order(StringComparer.Ordinal).ToList();
        }
    }

    public Google.Apis.Http.IHttpClientFactory ClientFactory => new Factory(this);

    public static HttpResponseMessage Error(HttpStatusCode status, string reason)
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = (int)status,
                ["message"] = reason,
                ["errors"] = new JsonArray(new JsonObject { ["reason"] = reason, ["message"] = reason }),
            },
        };
        return new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
        AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
        if (Intercept?.Invoke(request) is { } injected) return injected;

        Uri uri = request.RequestUri!;
        string path = uri.AbsolutePath;
        Dictionary<string, string> query = Query(uri);

        if (request.Method == HttpMethod.Get && path == "/drive/v3/files") return List(query);
        if (request.Method == HttpMethod.Get && path == "/drive/v3/changes/startPageToken") return Json(new JsonObject { ["startPageToken"] = Token() });
        if (request.Method == HttpMethod.Get && path == "/drive/v3/changes") return Changes(query);
        if (request.Method == HttpMethod.Get && path.StartsWith("/drive/v3/files/", StringComparison.Ordinal)) return Download(path["/drive/v3/files/".Length..]);
        if (request.Method == HttpMethod.Delete && path.StartsWith("/drive/v3/files/", StringComparison.Ordinal)) return Delete(path["/drive/v3/files/".Length..]);
        if (request.Method == HttpMethod.Post && path == "/upload/drive/v3/files") return await StartUploadAsync(null, request, cancellationToken);
        if (request.Method == HttpMethod.Patch && path.StartsWith("/upload/drive/v3/files/", StringComparison.Ordinal)) return await StartUploadAsync(path["/upload/drive/v3/files/".Length..], request, cancellationToken);
        if (request.Method == HttpMethod.Put && path.StartsWith("/session/", StringComparison.Ordinal)) return await CompleteUploadAsync(path["/session/".Length..], request, cancellationToken);
        return Error(HttpStatusCode.NotImplemented, "notImplemented");
    }

    private HttpResponseMessage List(Dictionary<string, string> query)
    {
        string q = query.GetValueOrDefault("q", string.Empty);
        string? scope = ScopeFilter().Match(q) is { Success: true } s ? Unquote(s.Groups[1].Value) : null;
        string? name = NameFilter().Match(q) is { Success: true } n ? Unquote(n.Groups[1].Value) : null;
        int offset = query.TryGetValue("pageToken", out string? token) ? int.Parse(token, CultureInfo.InvariantCulture) : 0;
        lock (_gate)
        {
            List<DriveEntry> matches = _files.Values
                .Where(f => scope is null || f.Scope == scope)
                .Where(f => name is null || f.Name == name)
                .OrderBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
            var page = matches.Skip(offset).Take(PageSize).ToList();
            var body = new JsonObject { ["files"] = new JsonArray(page.Select(Describe).ToArray<JsonNode?>()) };
            if (offset + PageSize < matches.Count) body["nextPageToken"] = (offset + PageSize).ToString(CultureInfo.InvariantCulture);
            return Json(body);
        }
    }

    private HttpResponseMessage Changes(Dictionary<string, string> query)
    {
        if (!long.TryParse(query.GetValueOrDefault("pageToken"), NumberStyles.None, CultureInfo.InvariantCulture, out long since))
        {
            return Error(HttpStatusCode.NotFound, "invalidPageToken");
        }

        lock (_gate)
        {
            var changes = _changes.Where(c => c.Sequence > since).Select(c => (JsonNode?)new JsonObject
            {
                ["fileId"] = c.FileId,
                ["removed"] = c.Removed,
                ["file"] = c.Removed ? null : new JsonObject { ["name"] = c.Name },
            }).ToArray();
            return Json(new JsonObject { ["changes"] = new JsonArray(changes), ["newStartPageToken"] = Token() });
        }
    }

    private HttpResponseMessage Download(string id)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(id, out DriveEntry? file)) return Error(HttpStatusCode.NotFound, "notFound");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Content) };
        }
    }

    private HttpResponseMessage Delete(string id)
    {
        lock (_gate)
        {
            if (!_files.Remove(id, out DriveEntry? file)) return Error(HttpStatusCode.NotFound, "notFound");
            _changes.Add((++_sequence, id, file.Name, true));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private async Task<HttpResponseMessage> StartUploadAsync(string? fileId, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string text = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancellationToken));
        JsonObject metadata = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text)!.AsObject();
        string session = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            if (fileId is not null && !_files.ContainsKey(fileId)) return Error(HttpStatusCode.NotFound, "notFound");
            _sessions[session] = (fileId, metadata);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Location = new Uri("https://www.googleapis.com/session/" + session);
        return response;
    }

    private async Task<HttpResponseMessage> CompleteUploadAsync(string session, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] content = await ReadBodyAsync(request, cancellationToken);
        lock (_gate)
        {
            if (!_sessions.Remove(session, out var upload)) return Error(HttpStatusCode.NotFound, "notFound");
            DateTimeOffset now = NextTime();
            DriveEntry entry;
            if (upload.FileId is { } existingId)
            {
                DriveEntry existing = _files[existingId];
                entry = existing with { Content = content, Version = existing.Version + 1, Modified = now };
            }
            else
            {
                string name = upload.Metadata["name"]?.GetValue<string>() ?? "unnamed";
                string? scope = upload.Metadata["appProperties"]?["staScope"]?.GetValue<string>();
                entry = new DriveEntry("f" + (++_ids).ToString("D5", CultureInfo.InvariantCulture), name, scope, content, 1, now, now);
            }

            _files[entry.Id] = entry;
            _changes.Add((++_sequence, entry.Id, entry.Name, false));
            return Json(Describe(entry));
        }
    }

    /// <summary>Request body, decompressed when the client sent it gzip-encoded (as the Google client does).</summary>
    private static async Task<byte[]> ReadBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null) return [];
        byte[] raw = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!request.Content.Headers.ContentEncoding.Contains("gzip")) return raw;
        using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress);
        using var plain = new MemoryStream();
        await gzip.CopyToAsync(plain, cancellationToken);
        return plain.ToArray();
    }

    private static JsonObject Describe(DriveEntry file) => new()
    {
        ["id"] = file.Id,
        ["name"] = file.Name,
        ["size"] = file.Content.Length.ToString(CultureInfo.InvariantCulture),
        ["version"] = file.Version.ToString(CultureInfo.InvariantCulture),
        ["createdTime"] = file.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        ["modifiedTime"] = file.Modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        ["sha256Checksum"] = Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant(),
    };

    private string Token()
    {
        lock (_gate) return _sequence.ToString(CultureInfo.InvariantCulture);
    }

    private DateTimeOffset NextTime() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++_clock);

    private static HttpResponseMessage Json(JsonObject body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> Query(Uri uri)
        => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty, StringComparer.Ordinal);

    private static string Unquote(string value) => value.Replace("\\'", "'", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    [GeneratedRegex(@"value='((?:[^'\\]|\\.)*)'")]
    private static partial Regex ScopeFilter();

    [GeneratedRegex(@"name = '((?:[^'\\]|\\.)*)'")]
    private static partial Regex NameFilter();

    private sealed record DriveEntry(string Id, string Name, string? Scope, byte[] Content, long Version, DateTimeOffset Created, DateTimeOffset Modified);

    private sealed class Factory(FakeDriveServer server) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => server;
    }
}

/// <summary>Token provider for tests; counts forced refreshes.</summary>
internal sealed class StaticTokenProvider : IGoogleAccessTokenProvider
{
    private int _refreshes;

    public int Refreshes => _refreshes;

    public Exception? Failure { get; set; }

    public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (Failure is not null) throw Failure;
        if (forceRefresh) Interlocked.Increment(ref _refreshes);
        return Task.FromResult("token-" + _refreshes.ToString(CultureInfo.InvariantCulture));
    }
}
