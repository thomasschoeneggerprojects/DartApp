namespace StreakCelebration.Services;

/// <summary>
/// Plays a celebration sound packaged as a MAUI raw asset (<c>Resources/Raw</c>), using the platform's media player.
/// Only one sound plays at a time; a new one replaces the previous one. Players are released when playback ends.
/// </summary>
public sealed class MauiCelebrationSoundPlayer : ICelebrationSoundPlayer, IDisposable
{
#if ANDROID
    private readonly Dictionary<string, string> _cachedFiles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private Android.Media.MediaPlayer? _player;
#elif IOS || MACCATALYST
    private AVFoundation.AVAudioPlayer? _player;
#elif WINDOWS
    private Windows.Media.Playback.MediaPlayer? _player;
#endif

    /// <inheritdoc />
    public async Task PlayAsync(string soundFile, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFile);
#if ANDROID
        // MediaPlayer needs a file path or descriptor; raw assets are copied to the cache once.
        string path = await GetCachedFileAsync(soundFile, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            Release();
            var player = new Android.Media.MediaPlayer();
            player.SetAudioAttributes(new Android.Media.AudioAttributes.Builder()
                .SetUsage(Android.Media.AudioUsageKind.Game)!
                .SetContentType(Android.Media.AudioContentType.Sonification)!
                .Build()!);
            player.SetDataSource(path);
            player.Completion += (_, _) => Release(player);
            player.Prepare();
            player.Start();
            _player = player;
        }).ConfigureAwait(false);
#elif IOS || MACCATALYST
        using Stream stream = await FileSystem.OpenAppPackageFileAsync(soundFile).ConfigureAwait(false);
        Foundation.NSData data = Foundation.NSData.FromStream(stream)
            ?? throw new InvalidOperationException($"Sound '{soundFile}' could not be read.");
        cancellationToken.ThrowIfCancellationRequested();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            // Ambient: mixes with other audio and respects the silent switch.
            AVFoundation.AVAudioSession.SharedInstance().SetCategory(AVFoundation.AVAudioSessionCategory.Ambient);
            Release();
            AVFoundation.AVAudioPlayer player = AVFoundation.AVAudioPlayer.FromData(data, out Foundation.NSError? error)
                ?? throw new InvalidOperationException($"Sound '{soundFile}' could not be decoded: {error?.LocalizedDescription}");
            player.FinishedPlaying += (_, _) => Release(player);
            player.PrepareToPlay();
            player.Play();
            _player = player;
        }).ConfigureAwait(false);
#elif WINDOWS
        var buffer = new MemoryStream();
        using (Stream stream = await FileSystem.OpenAppPackageFileAsync(soundFile).ConfigureAwait(false))
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        string contentType = ContentTypeFor(soundFile);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            Release();
            var player = new Windows.Media.Playback.MediaPlayer
            {
                Source = Windows.Media.Core.MediaSource.CreateFromStream(buffer.AsRandomAccessStream(), contentType),
            };
            player.MediaEnded += (_, _) => MainThread.BeginInvokeOnMainThread(() => Release(player));
            player.Play();
            _player = player;
        }).ConfigureAwait(false);
#else
        await Task.CompletedTask.ConfigureAwait(false);
#endif
    }

    /// <summary>Stops and releases the current player.</summary>
    public void Dispose()
    {
        if (MainThread.IsMainThread) Release();
        else MainThread.BeginInvokeOnMainThread(Release);
#if ANDROID
        _cacheLock.Dispose();
#endif
    }

#if ANDROID
    private async Task<string> GetCachedFileAsync(string soundFile, CancellationToken cancellationToken)
    {
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedFiles.TryGetValue(soundFile, out string? cached) && File.Exists(cached)) return cached;

            string directory = Path.Combine(FileSystem.CacheDirectory, "streak-celebration");
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, Path.GetFileName(soundFile));
            string temp = target + ".tmp";
            using (Stream source = await FileSystem.OpenAppPackageFileAsync(soundFile).ConfigureAwait(false))
            using (FileStream destination = File.Create(temp))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: true);
            _cachedFiles[soundFile] = target;
            return target;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private void Release() => Release(_player);

    private void Release(Android.Media.MediaPlayer? player)
    {
        if (player is null) return;
        if (ReferenceEquals(player, _player)) _player = null;
        try
        {
            if (player.IsPlaying) player.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Already stopped or never prepared.
        }

        player.Release();
        player.Dispose();
    }
#elif IOS || MACCATALYST
    private void Release() => Release(_player);

    private void Release(AVFoundation.AVAudioPlayer? player)
    {
        if (player is null) return;
        if (ReferenceEquals(player, _player)) _player = null;
        player.Stop();
        player.Dispose();
    }
#elif WINDOWS
    private void Release() => Release(_player);

    private void Release(Windows.Media.Playback.MediaPlayer? player)
    {
        if (player is null) return;
        if (ReferenceEquals(player, _player)) _player = null;
        player.Pause();
        player.Dispose();
    }

    private static string ContentTypeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".m4a" or ".aac" => "audio/aac",
        _ => "audio/mpeg",
    };
#else
    private static void Release()
    {
    }
#endif
}
