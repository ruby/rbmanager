using System.Diagnostics;

namespace RbManager.Tests.Support;

// Publishes the product once (AOT, self-contained, single-file) so the
// publish-only suite can exercise the real shipping artifact. AOT needs the
// MSVC linker, so a failed/absent publish yields a SkipReason rather than a
// hard failure. Shared across the collection via IClassFixture.
public sealed class PublishFixture : IDisposable
{
    private readonly string _outDir =
        Path.Combine(Path.GetTempPath(), "rbmanager-tests", $"publish-{Guid.NewGuid():N}");

    public string? ExePath { get; }
    public string? SkipReason { get; }

    public PublishFixture()
    {
        string? csproj = LocateCsproj();
        if (csproj is null)
        {
            SkipReason = "could not locate rbmanager.csproj from the test assembly";
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in new[]
                 { "publish", csproj, "-c", "Release", "-r", "win-x64", "-o", _outDir })
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)!;
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(300_000))
            {
                proc.Kill(entireProcessTree: true);
                SkipReason = "AOT publish timed out";
                return;
            }
            string exe = Path.Combine(_outDir, "rb.exe");
            if (proc.ExitCode == 0 && File.Exists(exe))
                ExePath = exe;
            else
                SkipReason =
                    $"AOT publish failed (exit {proc.ExitCode}): {Tail(errTask.Result + outTask.Result)}";
        }
        catch (Exception e)
        {
            SkipReason = $"AOT publish could not start: {e.Message}";
        }
    }

    // Repo root is the path segment before \rbmanager.Tests\.
    private static string? LocateCsproj()
    {
        string marker = $"{Path.DirectorySeparatorChar}rbmanager.Tests{Path.DirectorySeparatorChar}";
        int idx = AppContext.BaseDirectory.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        string repoRoot = AppContext.BaseDirectory[..idx];
        string csproj = Path.Combine(repoRoot, "rbmanager", "rbmanager.csproj");
        return File.Exists(csproj) ? csproj : null;
    }

    private static string Tail(string s) =>
        s.Length <= 400 ? s : s[^400..];

    public void Dispose() => TempDir.ForceDelete(_outDir);
}
