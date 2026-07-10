using Microsoft.Win32;

namespace RbManager.Tests.Support;

// A per-test writable subkey under HKCU\Software\rbmanager-tests\<guid>,
// deleted on Dispose. Never touches HKCU\Environment, so the real user PATH
// is untouched. Unique per instance, so tests using it run in parallel.
internal sealed class ScratchRegistryKey : IDisposable
{
    private const string Container = @"Software\rbmanager-tests";
    private readonly string _sub;

    public RegistryKey Key { get; }

    // The HKCU-relative path, in the form the RBMANAGER_* seams expect.
    public string SubKeyPath => _sub;

    public ScratchRegistryKey()
    {
        _sub = $@"{Container}\{Guid.NewGuid():N}";
        Key = Registry.CurrentUser.CreateSubKey(_sub, writable: true);
    }

    public void Dispose()
    {
        Key.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_sub, throwOnMissingSubKey: false); }
        catch { /* best effort */ }
    }
}
