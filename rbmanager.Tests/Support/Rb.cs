using System.Diagnostics;

namespace RbManager.Tests.Support;

internal readonly record struct RbResult(int ExitCode, string Out, string Err);

// Drives the built rb.exe as a child process. Environment redirection
// (RBMANAGER_ROOT / RBMANAGER_ENV_KEY) is applied to the child only, so the
// test process is untouched and E2E tests can run in parallel.
internal static class Rb
{
    public static string Exe { get; } = ResolveExe();

    public static RbResult Run(string root, string? envKey, params string[] args) =>
        RunExe(Exe, root, envKey, args);

    public static RbResult RunExe(string exe, string root, string? envKey, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["RBMANAGER_ROOT"] = root;
        if (envKey is not null) psi.Environment["RBMANAGER_ENV_KEY"] = envKey;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start rb.exe");
        // Read both streams concurrently to avoid a full-buffer deadlock.
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return new RbResult(proc.ExitCode, outTask.Result, errTask.Result);
    }

    // The test assembly runs from ...\rbmanager.Tests\bin\<cfg>\<tfm>\; the
    // product's apphost sits at the sibling ...\rbmanager\bin\<cfg>\<tfm>\.
    private static string ResolveExe()
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        string productDir = AppContext.BaseDirectory.Replace(
            $"{sep}rbmanager.Tests{sep}bin{sep}", $"{sep}rbmanager{sep}bin{sep}");
        string exe = Path.Combine(productDir, "rb.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException($"rb.exe not found at {exe}");
        return exe;
    }
}
