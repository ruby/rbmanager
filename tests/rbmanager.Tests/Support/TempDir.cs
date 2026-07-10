namespace RbManager.Tests.Support;

// A unique scratch directory under %TEMP%\rbmanager-tests, deleted on
// Dispose. Junction points and their targets both live inside it, so the
// recursive delete only ever removes test-owned paths.
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string? label = null)
    {
        string leaf = (label is null ? "" : label + "-") + Guid.NewGuid().ToString("N");
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rbmanager-tests", leaf);
        Directory.CreateDirectory(Path);
    }

    public string At(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    public void Dispose() => ForceDelete(Path);

    // Best-effort: cleanup failures must never fail a test. Junctions are
    // removed as reparse points by Directory.Delete without following them.
    public static void ForceDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
