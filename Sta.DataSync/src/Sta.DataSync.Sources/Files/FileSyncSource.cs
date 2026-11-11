using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sta.DataSync.Sources.Files;

/// <summary>Options of <see cref="FileSyncSource"/>.</summary>
public sealed class FileSyncSourceOptions
{
    /// <summary>Source id (and default scope id).</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Directory whose files are synchronized. Only this directory (and below) is ever read or written.</summary>
    public string RootDirectory { get; set; } = string.Empty;

    /// <summary>Glob patterns of synchronized relative paths (<c>*</c> within a segment, <c>**</c> across segments, <c>?</c>).</summary>
    public IList<string> Include { get; } = ["**"];

    /// <summary>Glob patterns excluded even when included.</summary>
    public IList<string> Exclude { get; } = [];

    /// <summary>Raise change notifications through a file system watcher.</summary>
    public bool WatchForChanges { get; set; }

    /// <summary>Returns all validation errors.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!SyncPath.IsValid(SourceId) || SourceId.Contains('/', StringComparison.Ordinal)) errors.Add("SourceId must be a single valid path segment.");
        if (string.IsNullOrWhiteSpace(RootDirectory) || !Path.IsPathFullyQualified(RootDirectory)) errors.Add("RootDirectory must be an absolute path.");
        if (Include.Count == 0) errors.Add("Include needs at least one pattern.");
        return errors;
    }
}

/// <summary>
/// Synchronizes files below a directory. Ids are relative paths with '/' separators. Reads stream with shared access;
/// writes go to a temporary file in the target directory, are flushed to disk and only then replace the target by rename,
/// so an interrupted or invalid download never damages the existing file.
/// </summary>
public sealed class FileSyncSource : ISyncDataSource, ISyncChangeTracking, IDisposable
{
    /// <summary>Suffix of temporary files (never synchronized).</summary>
    public const string TemporarySuffix = ".sta-tmp";

    private const int BufferSize = 81920;
    private readonly string _root;
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private FileSystemWatcher? _watcher;

    /// <summary>Creates the source.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public FileSyncSource(FileSyncSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> errors = options.Validate();
        if (errors.Count > 0) throw new ArgumentException("Invalid file source options: " + string.Join(" ", errors), nameof(options));

        SourceId = options.SourceId;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDirectory));
        _include = options.Include.Select(Glob.ToRegex).ToArray();
        _exclude = options.Exclude.Select(Glob.ToRegex).ToArray();
        if (options.WatchForChanges) StartWatching();
    }

    /// <inheritdoc />
    public event EventHandler<SyncSourceChangedEventArgs>? Changed;

    /// <inheritdoc />
    public string SourceId { get; }

    /// <inheritdoc />
    public SyncDataSourceCapabilities Capabilities => SyncDataSourceCapabilities.Write | SyncDataSourceCapabilities.Delete | SyncDataSourceCapabilities.CreateItems;

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncItem>> GetItemsAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<SyncItem>>(() => Enumerate(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string itemId, CancellationToken cancellationToken = default)
    {
        string path = Resolve(itemId);
        try
        {
            return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SyncItemNotFoundException($"'{itemId}' does not exist.", ex);
        }
    }

    /// <inheritdoc />
    public async Task ApplyChangeAsync(SyncChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        string path = Resolve(change.ItemId);
        if (change.Kind == SyncChangeKind.Delete)
        {
            EnsureExpected(change, path);
            File.Delete(path);
            return;
        }

        if (change.Content is null) throw new ArgumentException("A write needs content.", nameof(change));
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + TemporarySuffix);
        try
        {
            await WriteTemporaryAsync(temporary, change.Content, cancellationToken).ConfigureAwait(false);
            EnsureExpected(change, path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Stops watching.</summary>
    public void Dispose() => _watcher?.Dispose();

    /// <summary>Fingerprint used for cheap change detection: length and last write time.</summary>
    internal static string FingerprintOf(FileInfo file)
        => file.Length.ToString(CultureInfo.InvariantCulture) + ":" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    private List<SyncItem> Enumerate(CancellationToken cancellationToken)
    {
        var items = new List<SyncItem>();
        if (!Directory.Exists(_root)) return items;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(_root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!IsSynchronized(id)) continue;

            var info = new FileInfo(file);
            items.Add(new SyncItem(id, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), FingerprintOf(info)));
        }

        return items;
    }

    private bool IsSynchronized(string id)
        => !id.EndsWith(TemporarySuffix, StringComparison.Ordinal)
            && SyncPath.IsValid(id)
            && _include.Any(r => r.IsMatch(id))
            && !_exclude.Any(r => r.IsMatch(id));

    /// <summary>Maps an id to a path below the root; rejects traversal and filtered ids.</summary>
    private string Resolve(string itemId)
    {
        SyncPath.Validate(itemId);
        if (!IsSynchronized(itemId)) throw new SyncConfigurationException($"'{itemId}' is not part of file source '{SourceId}'.");

        string full = Path.GetFullPath(Path.Combine(_root, itemId.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, _pathComparison))
        {
            throw new SyncConfigurationException($"'{itemId}' resolves outside of the source directory.");
        }

        return full;
    }

    private static async Task WriteTemporaryAsync(string temporary, Stream content, CancellationToken cancellationToken)
    {
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            // The content stream verifies its hash at the end; a mismatch throws before the target is touched.
            await content.CopyToAsync(stream, BufferSize, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void EnsureExpected(SyncChange change, string path)
    {
        var file = new FileInfo(path);
        if (change.ExpectNotExists)
        {
            if (file.Exists) throw new SyncConcurrencyException($"'{change.ItemId}' was created locally during synchronization.");
            return;
        }

        if (change.ExpectedCurrentFingerprint is { } expected && (!file.Exists || FingerprintOf(file) != expected))
        {
            throw new SyncConcurrencyException($"'{change.ItemId}' was changed locally during synchronization; it is synchronized next time.");
        }
    }

    private void StartWatching()
    {
        Directory.CreateDirectory(_root);
        _watcher = new FileSystemWatcher(_root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName };
        FileSystemEventHandler handler = (_, e) => OnFileChanged(e.FullPath);
        _watcher.Changed += handler;
        _watcher.Created += handler;
        _watcher.Deleted += handler;
        _watcher.Renamed += (_, e) => OnFileChanged(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged(string fullPath)
    {
        string id = Path.GetRelativePath(_root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (IsSynchronized(id)) Changed?.Invoke(this, new SyncSourceChangedEventArgs(SourceId, id));
    }

    /// <summary>Minimal glob to regex translation for relative paths.</summary>
    private static class Glob
    {
        public static Regex ToRegex(string pattern)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
            var builder = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    bool slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    builder.Append(slash ? "(?:.*/)?" : ".*");
                    i += slash ? 2 : 1;
                }
                else if (c == '*')
                {
                    builder.Append("[^/]*");
                }
                else if (c == '?')
                {
                    builder.Append("[^/]");
                }
                else
                {
                    builder.Append(Regex.Escape(c.ToString()));
                }
            }

            return new Regex(builder.Append('$').ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        }
    }
}
