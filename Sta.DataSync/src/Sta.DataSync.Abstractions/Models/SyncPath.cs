namespace Sta.DataSync;

/// <summary>
/// Rules for logical item ids and remote paths: relative, '/'-separated, no empty, <c>.</c> or <c>..</c> segments,
/// no backslashes, drive letters or control characters. Platform-independent and safe against path traversal.
/// </summary>
public static class SyncPath
{
    /// <summary>Longest accepted path.</summary>
    public const int MaxLength = 1024;

    private static readonly System.Buffers.SearchValues<char> InvalidCharacters = System.Buffers.SearchValues.Create("\\:*?\"<>|");

    /// <summary>Converts '\' to '/', trims separators and validates the result.</summary>
    /// <exception cref="ArgumentException">The path is invalid.</exception>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string normalized = path.Replace('\\', '/').Trim('/');
        Validate(normalized);
        return normalized;
    }

    /// <summary>Validates an already normalized path.</summary>
    /// <exception cref="ArgumentException">The path is invalid.</exception>
    public static void Validate(string path)
    {
        if (!TryValidate(path, out string? error)) throw new ArgumentException(error, nameof(path));
    }

    /// <summary>Checks a path without throwing.</summary>
    public static bool IsValid(string? path) => TryValidate(path, out _);

    /// <summary>Joins validated segments with '/'.</summary>
    public static string Combine(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        string joined = string.Join('/', segments.Where(s => !string.IsNullOrEmpty(s)).Select(s => s.Trim('/')));
        Validate(joined);
        return joined;
    }

    private static bool TryValidate(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) error = "The path is empty.";
        else if (path.Length > MaxLength) error = $"The path is longer than {MaxLength} characters.";
        else if (path[0] == '/' || path[^1] == '/') error = "The path must be relative and must not end with '/'.";
        else if (path.AsSpan().ContainsAny(InvalidCharacters)) error = "The path contains an invalid character.";
        else if (path.Any(char.IsControl)) error = "The path contains a control character.";
        else if (path.Split('/').Any(s => s.Length == 0 || s == "." || s == ".." || s.Trim().Length != s.Length))
        {
            error = "The path contains an empty, '.', '..' or space-padded segment.";
        }

        return error is null;
    }
}
