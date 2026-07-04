using Microsoft.Win32;

namespace RbManager.Tests.Support;

// A per-test scratch root and HKCU subkey for E2E runs. Unlike RbSandbox it
// does not touch the test process environment; the redirection is passed to
// each child rb.exe invocation, so E2E tests run in parallel.
internal sealed class E2eSandbox : IDisposable
{
    private readonly TempDir _tmp = new("e2e");

    public string Root => _tmp.Path;
    public string Rubies => Path.Combine(Root, "rubies");
    public string EnvKey { get; } = $@"Software\rbmanager-tests\{Guid.NewGuid():N}";

    public RbResult Run(params string[] args) => Rb.Run(Root, EnvKey, args);
    public RbResult RunExe(string exe, params string[] args) => Rb.RunExe(exe, Root, EnvKey, args);

    // A ruby-* fixture zip written under the sandbox (outside rubies).
    public string MakeZip(string rootName) =>
        Zips.WriteRuby(Path.Combine(Root, "_src", $"{rootName}.zip"), rootName);

    public string? WrittenPath()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(EnvKey);
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string;
    }

    public void Dispose()
    {
        _tmp.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(EnvKey, throwOnMissingSubKey: false); }
        catch { /* best effort */ }
    }
}
