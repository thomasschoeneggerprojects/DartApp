using System.Buffers;
using System.Security.Cryptography;

namespace Sta.DataSync;

/// <summary>SHA-256 content hashes in the form <c>sha256:&lt;64 lowercase hex digits&gt;</c>.</summary>
public static class ContentHash
{
    /// <summary>Prefix of every hash.</summary>
    public const string Prefix = "sha256:";

    private const int BufferSize = 81920;

    /// <summary>Formats a raw SHA-256 digest.</summary>
    public static string Format(ReadOnlySpan<byte> digest) => Prefix + Convert.ToHexString(digest).ToLowerInvariant();

    /// <summary><see langword="true"/> for a well-formed hash.</summary>
    public static bool IsValid(string? hash)
        => hash is { Length: 71 } && hash.StartsWith(Prefix, StringComparison.Ordinal) && hash.AsSpan(7).ContainsOnlyHex();

    /// <summary>The hex part, usable as a file or object name.</summary>
    public static string HexOf(string hash)
    {
        if (!IsValid(hash)) throw new ArgumentException("Not a valid content hash.", nameof(hash));
        return hash[Prefix.Length..];
    }

    /// <summary>Hashes a stream to its end without loading it into memory.</summary>
    public static async Task<(string Hash, long Length)> ComputeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long length = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                length += read;
            }

            return (Format(hash.GetHashAndReset()), length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)) return false;
        }

        return true;
    }
}

