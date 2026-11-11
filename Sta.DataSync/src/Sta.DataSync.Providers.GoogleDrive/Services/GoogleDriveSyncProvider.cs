using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Upload;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sta.DataSync.Providers.GoogleDrive.Api;
using Sta.DataSync.Providers.GoogleDrive.Authentication;
using Sta.DataSync.Providers.GoogleDrive.Mapping;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Sta.DataSync.Providers.GoogleDrive.Services;

/// <summary>Options of <see cref="GoogleDriveSyncProvider"/>.</summary>
public sealed class GoogleDriveProviderOptions
{
    /// <summary>Application name sent to Google (user agent).</summary>
    public string ApplicationName { get; set; } = "Sta.DataSync";

    /// <summary>Upload chunk size for resumable uploads (multiple of 256 KB).</summary>
    public int UploadChunkSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>Items per list request (1-1000).</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Returns validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ApplicationName)) errors.Add("ApplicationName is required.");
        if (UploadChunkSize < ResumableUpload.MinimumChunkSize || UploadChunkSize % ResumableUpload.MinimumChunkSize != 0)
        {
            errors.Add($"UploadChunkSize must be a positive multiple of {ResumableUpload.MinimumChunkSize} bytes.");
        }

        if (PageSize is < 1 or > 1000) errors.Add("PageSize must be between 1 and 1000.");
        return errors;
    }
}

/// <summary>
/// Google Drive destination in the application's hidden <c>appDataFolder</c> (scope <c>drive.appdata</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Logical paths are stored as file names; the scope prefix is an app property, so listings are filtered server-side
/// and paged. <c>appDataFolder</c> belongs to the OAuth client: separately registered apps never see each other's data,
/// and the same app must use the same Google Cloud project on every platform to share it.</item>
/// <item>Drive v3 has no compare-and-swap for files. The provider therefore reports
/// <see cref="SyncProviderCapabilities.ServerCreationOrder"/> (server creation times) and the engine uses its
/// revision-log protocol; <see cref="RemoteUpload.ExpectedVersion"/> is rejected.</item>
/// <item>Uploads are resumable within a run (chunked); an upload interrupted by process termination restarts next run.</item>
/// <item>Change detection uses Drive change tokens (<see cref="IRemoteChangeFeed"/>).</item>
/// </list>
/// </remarks>
public sealed partial class GoogleDriveSyncProvider : ISyncProvider, IRemoteChangeFeed, IDisposable
{
    /// <summary>Provider id.</summary>
    public const string Id = "google-drive";

    private const string Space = "appDataFolder";
    private const string ContentType = "application/octet-stream";

    private readonly DriveService _drive;
    private readonly GoogleDriveProviderOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the provider.</summary>
    public GoogleDriveSyncProvider(IGoogleAccessTokenProvider tokens, IOptions<GoogleDriveProviderOptions> options, ILogger<GoogleDriveSyncProvider>? logger = null)
        : this(tokens, options, logger, null)
    {
    }

    internal GoogleDriveSyncProvider(IGoogleAccessTokenProvider tokens, IOptions<GoogleDriveProviderOptions> options, ILogger? logger, Google.Apis.Http.IHttpClientFactory? httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        IReadOnlyList<string> errors = _options.Validate();
        if (errors.Count > 0) throw new SyncConfigurationException("Invalid Google Drive options: " + string.Join(" ", errors));
        _logger = logger ?? NullLogger.Instance;
        _drive = DriveClientFactory.Create(tokens, _options.ApplicationName, httpClientFactory);
    }

    /// <inheritdoc />
    public string ProviderId => Id;

    /// <inheritdoc />
    public SyncProviderCapabilities Capabilities => SyncProviderCapabilities.ServerCreationOrder | SyncProviderCapabilities.ChangeFeed
        | SyncProviderCapabilities.ContentHashes | SyncProviderCapabilities.ResumableUploads;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteSyncItem>> ListItemsAsync(string pathPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathPrefix);
        string? scope = DriveMapping.ScopeOf(pathPrefix);
        string query = "trashed = false" + (scope is null ? string.Empty : $" and appProperties has {{ key='{DriveMapping.ScopeProperty}' and value={DriveMapping.Quote(scope)} }}");
        List<RemoteSyncItem> items = await QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return items.Where(i => i.Path.StartsWith(pathPrefix, StringComparison.Ordinal)).ToList();
    }

    /// <inheritdoc />
    public async Task<RemoteSyncItem?> GetItemAsync(string path, CancellationToken cancellationToken = default)
    {
        SyncPath.Validate(path);
        List<RemoteSyncItem> matches = await QueryAsync($"trashed = false and name = {DriveMapping.Quote(path)}", cancellationToken).ConfigureAwait(false);
        return matches.OrderBy(i => i.CreatedUtc ?? DateTimeOffset.MaxValue).ThenBy(i => i.ProviderItemId, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<Stream> DownloadAsync(RemoteSyncItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        LogRequest(_logger, "download", item.Path);
        try
        {
            // Raw media request: the response is streamed to the caller instead of buffered.
            var uri = new Uri(_drive.BaseUri + "files/" + Uri.EscapeDataString(item.ProviderItemId) + "?alt=media");
            HttpResponseMessage response = await _drive.HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Google.Apis.Requests.RequestError error = await _drive.DeserializeError(response).ConfigureAwait(false);
                var exception = new Google.GoogleApiException(_drive.Name, error?.Message ?? "Download failed.")
                {
                    Error = error,
                    HttpStatusCode = response.StatusCode,
                };
                response.Dispose();
                throw exception;
            }

            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not SyncException && !IsCallerCancellation(ex, cancellationToken))
        {
            throw DriveMapping.Map(ex, "download");
        }
    }

    /// <inheritdoc />
    public async Task<RemoteSyncItem> UploadAsync(RemoteUpload upload, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(content);
        SyncPath.Validate(upload.Path);
        if (upload.ExpectedVersion is not null)
        {
            throw new SyncConfigurationException("Google Drive does not support conditional writes; the engine must not request them.");
        }

        LogRequest(_logger, "upload", upload.Path);
        RemoteSyncItem? existing = await GetItemAsync(upload.Path, cancellationToken).ConfigureAwait(false);
        ResumableUpload<DriveFile, DriveFile> request = existing is null ? CreateRequest(upload, content) : UpdateRequest(existing, content);
        request.ChunkSize = _options.UploadChunkSize;

        IUploadProgress progress = await request.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (progress.Status != UploadStatus.Completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw DriveMapping.Map(progress.Exception ?? new IOException("The upload did not complete."), "upload");
        }

        DriveFile? stored = request switch
        {
            FilesResource.CreateMediaUpload create => create.ResponseBody,
            FilesResource.UpdateMediaUpload update => update.ResponseBody,
            _ => null,
        };
        return (stored is null ? null : DriveMapping.ToRemoteItem(stored))
            ?? throw new SyncProviderException($"Google Drive returned no metadata for '{upload.Path}'.");
    }

    /// <inheritdoc />
    public async Task DeleteAsync(RemoteSyncItem item, string? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (expectedVersion is not null) throw new SyncConfigurationException("Google Drive does not support conditional deletes.");
        LogRequest(_logger, "delete", item.Path);
        try
        {
            await _drive.Files.Delete(item.ProviderItemId).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone.
        }
        catch (Exception ex) when (ex is not SyncException && !IsCallerCancellation(ex, cancellationToken))
        {
            throw DriveMapping.Map(ex, "delete");
        }
    }

    /// <inheritdoc />
    public async Task<string> GetStartTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            StartPageToken token = await _drive.Changes.GetStartPageToken().ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return token.StartPageTokenValue ?? throw new SyncProviderException("Google Drive returned no start page token.");
        }
        catch (Exception ex) when (ex is not SyncException && !IsCallerCancellation(ex, cancellationToken))
        {
            throw DriveMapping.Map(ex, "change token");
        }
    }

    /// <inheritdoc />
    public async Task<RemoteChangeSet> GetChangesAsync(string token, string pathPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        bool changed = false;
        string? pageToken = token;
        try
        {
            while (true)
            {
                ChangesResource.ListRequest request = _drive.Changes.List(pageToken);
                request.Spaces = Space;
                request.IncludeRemoved = true;
                request.PageSize = _options.PageSize;
                request.Fields = "nextPageToken, newStartPageToken, changes(fileId, removed, file(name))";
                ChangeList page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                // Removed files have no name any more; count them conservatively as changes.
                changed |= page.Changes?.Any(c => c.Removed == true || (c.File?.Name?.StartsWith(pathPrefix, StringComparison.Ordinal) ?? false)) ?? false;
                if (page.NewStartPageToken is { } next) return new RemoteChangeSet(changed, next);
                pageToken = page.NextPageToken ?? throw new SyncProviderException("Google Drive returned neither a next page nor a new start token.");
            }
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound)
        {
            throw new SyncConcurrencyException("The Google Drive change token is no longer valid.", ex);
        }
        catch (Exception ex) when (ex is not SyncException && !IsCallerCancellation(ex, cancellationToken))
        {
            throw DriveMapping.Map(ex, "change listing");
        }
    }

    /// <summary>Releases the HTTP resources.</summary>
    public void Dispose() => _drive.Dispose();

    private async Task<List<RemoteSyncItem>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        var items = new List<RemoteSyncItem>();
        string? pageToken = null;
        try
        {
            do
            {
                FilesResource.ListRequest request = _drive.Files.List();
                request.Spaces = Space;
                request.Q = query;
                request.PageSize = _options.PageSize;
                request.PageToken = pageToken;
                request.Fields = $"nextPageToken, files({DriveMapping.FileFields})";
                FileList page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                foreach (DriveFile file in page.Files ?? [])
                {
                    if (DriveMapping.ToRemoteItem(file) is { } item) items.Add(item);
                }

                pageToken = page.NextPageToken;
            }
            while (pageToken is not null);
        }
        catch (Exception ex) when (ex is not SyncException && !IsCallerCancellation(ex, cancellationToken))
        {
            throw DriveMapping.Map(ex, "listing");
        }

        return items;
    }

    private FilesResource.CreateMediaUpload CreateRequest(RemoteUpload upload, Stream content)
    {
        var metadata = new DriveFile { Name = upload.Path, Parents = [Space] };
        if (DriveMapping.ScopeOf(upload.Path) is { } scope) metadata.AppProperties = new Dictionary<string, string> { [DriveMapping.ScopeProperty] = scope };
        FilesResource.CreateMediaUpload request = _drive.Files.Create(metadata, content, ContentType);
        request.Fields = DriveMapping.FileFields;
        return request;
    }

    private FilesResource.UpdateMediaUpload UpdateRequest(RemoteSyncItem existing, Stream content)
    {
        FilesResource.UpdateMediaUpload request = _drive.Files.Update(new DriveFile(), existing.ProviderItemId, content, ContentType);
        request.Fields = DriveMapping.FileFields;
        return request;
    }

    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken)
        => exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    [LoggerMessage(EventId = 3100, Level = LogLevel.Debug, Message = "Google Drive {Operation}: {Path}")]
    private static partial void LogRequest(ILogger logger, string operation, string path);

    /// <summary>Response body stream that disposes its HTTP response.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
