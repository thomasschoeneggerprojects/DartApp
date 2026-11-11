namespace SportTA.Streaks.Tests.Infrastructure;

/// <summary>A unique temporary directory standing in for the host application's data directory.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SportTA.Streaks.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string FileFor(Guid playerId) => System.IO.Path.Combine(Path, $"streaks-{playerId:N}.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
