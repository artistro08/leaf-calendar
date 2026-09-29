namespace LeafCalendar.Tests.Support;

/// <summary>A unique temp folder deleted on dispose.</summary>
public sealed class TempFolder : IDisposable
{
    /// <summary>The folder path (created).</summary>
    public string Path { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "leaf-tests", Guid.NewGuid().ToString("N"))).FullName;

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file is still open (e.g. SQLite pool); the OS temp cleaner will remove it.
        }
    }
}
