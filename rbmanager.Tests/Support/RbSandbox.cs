using Microsoft.Win32;

namespace RbManager.Tests.Support;

// A redirected %LOCALAPPDATA%\Ruby for in-process Program tests: a temp root
// wired to RBMANAGER_ROOT, plus a scratch HKCU subkey wired to
// RBMANAGER_ENV_KEY so UserPath.Ensure never touches the real user PATH.
// Only safe inside the Serial collection (it mutates process env vars).
internal sealed class RbSandbox : IDisposable
{
    private readonly TempDir _tmp = new("root");
    private readonly EnvScope _env = new();
    private readonly string _regSub;

    public string Root => _tmp.Path;
    public string Rubies => Path.Combine(Root, "rubies");
    public string Current => Path.Combine(Root, "current");

    public RbSandbox()
    {
        _regSub = $@"Software\rbmanager-tests\{Guid.NewGuid():N}";
        _env.Set("RBMANAGER_ROOT", Root);
        _env.Set("RBMANAGER_ENV_KEY", _regSub);
    }

    // Creates rubies\<name> (with a bin subdir) without going through install.
    public string Seed(string name)
    {
        string dir = Path.Combine(Rubies, name);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        return dir;
    }

    public void Seed(params string[] names)
    {
        foreach (string n in names) Seed(n);
    }

    // Points the current junction at rubies\<name>, as SwitchTo would.
    public void Activate(string name) =>
        Junction.Create(Current, Path.Combine(Rubies, name));

    // The raw PATH value UserPath.Ensure wrote to the scratch key (or null).
    public string? WrittenPath()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_regSub);
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string;
    }

    public void Dispose()
    {
        _env.Dispose();
        _tmp.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_regSub, throwOnMissingSubKey: false); }
        catch { /* best effort */ }
    }
}
