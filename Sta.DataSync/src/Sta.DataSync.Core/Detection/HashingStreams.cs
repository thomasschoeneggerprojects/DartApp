using System.Security.Cryptography;

namespace Sta.DataSync;

/// <summary>Read-only pass-through stream that hashes and counts everything read through it.</summary>
internal class HashingReadStream(Stream inner, bool leaveOpen = false) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _result;

    public long BytesRead { get; private set; }

    /// <summary>Hash of the content read so far; only meaningful after the end was reached.</summary>
    public string Hash => _result ??= ContentHash.Format(_hash.GetCurrentHash());

    public bool ReachedEnd { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        OnRead(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        OnRead(buffer.Span[..read]);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected virtual void OnEnd()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            if (!leaveOpen) inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        if (!leaveOpen) await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnRead(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            if (!ReachedEnd)
            {
                ReachedEnd = true;
                OnEnd();
            }

            return;
        }

        _result = null;
        _hash.AppendData(data);
        BytesRead += data.Length;
    }
}

/// <summary>
/// Stream given to data sources with downloaded content: verifies length and hash when its end is reached and throws
/// <see cref="SyncIntegrityException"/> on a mismatch, so a source never replaces data with corrupt content.
/// </summary>
internal sealed class VerifyingReadStream(Stream inner, string expectedHash, long expectedLength) : HashingReadStream(inner)
{
    public override int Read(Span<byte> buffer)
    {
        int read = base.Read(buffer);
        ThrowIfTooLong();
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        ThrowIfTooLong();
        return read;
    }

    protected override void OnEnd()
    {
        if (BytesRead != expectedLength)
        {
            throw new SyncIntegrityException($"Downloaded content has {BytesRead} bytes; {expectedLength} were expected.");
        }

        if (!string.Equals(Hash, expectedHash, StringComparison.Ordinal))
        {
            throw new SyncIntegrityException("Downloaded content does not match its SHA-256 hash.");
        }
    }

    private void ThrowIfTooLong()
    {
        if (BytesRead > expectedLength)
        {
            throw new SyncIntegrityException($"Downloaded content is longer than the expected {expectedLength} bytes.");
        }
    }
}
