using Microsoft.Win32;

namespace RbManager.Tests.Support;

// A per-test scratch root and HKCU subkey for E2E runs. Unlike RbSandbox it
// does not touch the test process environment; the redirection is passed to
// each child rb.exe invocation, so E2E tests run in parallel. The VC++
// Redistributable probe is redirected to a scratch subkey seeded as
// "installed" (and the DLL fallback to a nonexistent path), so children
// never consult the host machine's real state.
internal sealed class E2eSandbox : IDisposable
{
    private readonly TempDir _tmp = new("e2e");
    private readonly string _vcSub = $@"Software\rbmanager-tests\{Guid.NewGuid():N}";
    private readonly Dictionary<string, string> _extraEnv;

    public string Root => _tmp.Path;
    public string Rubies => Path.Combine(Root, "rubies");
    public string EnvKey { get; } = $@"Software\rbmanager-tests\{Guid.NewGuid():N}";

    public E2eSandbox()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_vcSub, writable: true);
        key.SetValue("Installed", 1, RegistryValueKind.DWord);
        key.SetValue("Version", "v14.99.0.0");
        _extraEnv = new Dictionary<string, string>
        {
            ["RBMANAGER_VC_REDIST_KEY"] = _vcSub,
            ["RBMANAGER_VCRUNTIME"] = Path.Combine(Root, "no-vcruntime140.dll"),
        };
    }

    public RbResult Run(params string[] args) => RunExe(Rb.Exe, args);
    public RbResult RunExe(string exe, params string[] args) =>
        Rb.RunExe(exe, Root, EnvKey, _extraEnv, args);

    // Flips the sandbox to look like a machine without the VC++ runtime.
    public void RemoveVcRedist() =>
        Registry.CurrentUser.DeleteSubKeyTree(_vcSub, throwOnMissingSubKey: false);

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
        foreach (string sub in new[] { EnvKey, _vcSub })
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); }
            catch { /* best effort */ }
        }
    }
}
