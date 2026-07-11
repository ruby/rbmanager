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
        RunExe(Exe, root, envKey, null, args);

    public static RbResult RunExe(string exe, string root, string? envKey,
        IReadOnlyDictionary<string, string>? extraEnv, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["RBMANAGER_ROOT"] = root;
        if (envKey is not null) psi.Environment["RBMANAGER_ENV_KEY"] = envKey;
        if (extraEnv is not null)
            foreach ((string k, string v) in extraEnv) psi.Environment[k] = v;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start rb.exe");
        // Closed stdin so any prompt reads EOF instead of hanging the test.
        proc.StandardInput.Close();
        // Read both streams concurrently to avoid a full-buffer deadlock.
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return new RbResult(proc.ExitCode, outTask.Result, errTask.Result);
    }

    // With the artifacts output layout the test assembly runs from
    // ...\artifacts\bin\rbmanager.Tests\<cfg>\; the product's apphost sits
    // at the sibling ...\artifacts\bin\rbmanager\<cfg>\.
    private static string ResolveExe()
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        string productDir = AppContext.BaseDirectory.Replace(
            $"{sep}bin{sep}rbmanager.Tests{sep}", $"{sep}bin{sep}rbmanager{sep}");
        string exe = Path.Combine(productDir, "rb.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException($"rb.exe not found at {exe}");
        return exe;
    }
}
