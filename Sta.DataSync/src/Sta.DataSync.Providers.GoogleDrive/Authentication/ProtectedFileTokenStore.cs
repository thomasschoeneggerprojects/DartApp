using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sta.DataSync.Providers.GoogleDrive.Authentication;

/// <summary>
/// Token store for Windows desktop applications: tokens are encrypted with DPAPI for the current Windows user and
/// written atomically to a file. Use platform secure storage (e.g. MAUI <c>SecureStorage</c>) on other platforms.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProtectedFileTokenStore(string filePath) : IGoogleTokenStore
{
    private static readonly byte[] Entropy = "Sta.DataSync.GoogleDrive.v1"u8.ToArray();
    private readonly string _filePath = Path.GetFullPath(filePath);

    /// <inheritdoc />
    public async Task<GoogleTokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return null;
        byte[] encrypted = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<GoogleTokenSet>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Unreadable (other user, corrupt): treat as signed out.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(GoogleTokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        byte[] encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string temporary = _filePath + ".tmp";
        await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _filePath, overwrite: true);
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
        return Task.CompletedTask;
    }
}
