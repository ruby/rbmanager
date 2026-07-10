using Microsoft.Win32;
using RbManager.Tests.Support;

namespace RbManager.Tests;

// VC++ Redistributable detection and the rb setup consent flow. Registry
// state is simulated through RBMANAGER_VC_REDIST_KEY (an HKCU scratch
// subkey) and the DLL fallback through RBMANAGER_VCRUNTIME, so nothing here
// reads the host machine's real installation state. The install path itself
// (download, signature check, elevation) is not driven end to end; the
// signature verifier is covered by the unsigned-rejection case here and by
// the opt-in Network test against the real installer.
[Trait("Category", "Unit")]
[Collection(Serial.Name)]
public class VcRedistTests
{
    private const string Version = "v14.99.1.0";

    private static void SeedInstalled(ScratchRegistryKey key, int installed = 1)
    {
        key.Key.SetValue("Installed", installed, RegistryValueKind.DWord);
        key.Key.SetValue("Version", Version);
    }

    // Points both seams at empty/nonexistent targets: a machine without the
    // redistributable. Returns the scope; dispose to restore.
    private static EnvScope MissingRedist(TempDir tmp)
    {
        var env = new EnvScope();
        env.Set("RBMANAGER_VC_REDIST_KEY",
            $@"Software\rbmanager-tests\{Guid.NewGuid():N}");
        env.Set("RBMANAGER_VCRUNTIME", Path.Combine(tmp.Path, "no-vcruntime140.dll"));
        return env;
    }

    private sealed class StdinScope : IDisposable
    {
        private readonly TextReader _prev = Console.In;
        public StdinScope(string input) => Console.SetIn(new StringReader(input));
        public void Dispose() => Console.SetIn(_prev);
    }

    [Fact]
    public void InstalledVersion_InstalledOne_ReturnsVersion()
    {
        using var key = new ScratchRegistryKey();
        using var env = new EnvScope();
        SeedInstalled(key);
        env.Set("RBMANAGER_VC_REDIST_KEY", key.SubKeyPath);

        Assert.Equal(Version, VcRedist.InstalledVersion());
    }

    [Fact]
    public void InstalledVersion_InstalledZero_ReturnsNull()
    {
        using var key = new ScratchRegistryKey();
        using var env = new EnvScope();
        SeedInstalled(key, installed: 0);
        env.Set("RBMANAGER_VC_REDIST_KEY", key.SubKeyPath);

        Assert.Null(VcRedist.InstalledVersion());
    }

    [Fact]
    public void InstalledVersion_MissingKey_ReturnsNull()
    {
        using var env = new EnvScope();
        env.Set("RBMANAGER_VC_REDIST_KEY",
            $@"Software\rbmanager-tests\{Guid.NewGuid():N}");

        Assert.Null(VcRedist.InstalledVersion());
    }

    [Fact]
    public void InstalledVersion_NoVersionValue_ReturnsPlaceholder()
    {
        using var key = new ScratchRegistryKey();
        using var env = new EnvScope();
        key.Key.SetValue("Installed", 1, RegistryValueKind.DWord);
        env.Set("RBMANAGER_VC_REDIST_KEY", key.SubKeyPath);

        Assert.Equal("(unknown version)", VcRedist.InstalledVersion());
    }

    [Fact]
    public void IsInstalled_RegistryMissing_FallsBackToDll()
    {
        using var tmp = new TempDir("vcr");
        using var env = MissingRedist(tmp);
        Assert.False(VcRedist.IsInstalled());

        string dll = Path.Combine(tmp.Path, "vcruntime140.dll");
        File.WriteAllBytes(dll, [0]);
        env.Set("RBMANAGER_VCRUNTIME", dll);
        Assert.True(VcRedist.IsInstalled());
    }

    [Fact]
    public async Task Ensure_AlreadyInstalled_ReportsAndReturnsZero()
    {
        using var key = new ScratchRegistryKey();
        using var env = new EnvScope();
        using var cap = new ConsoleCapture();
        SeedInstalled(key);
        env.Set("RBMANAGER_VC_REDIST_KEY", key.SubKeyPath);

        int rc = await VcRedist.Ensure(assumeYes: false);

        Assert.Equal(0, rc);
        Assert.Contains($"VC++ Redistributable {Version} is already installed", cap.Out);
    }

    [Fact]
    public async Task Ensure_DllPresentWithoutRegistry_ReturnsZero()
    {
        using var tmp = new TempDir("vcr");
        using var env = MissingRedist(tmp);
        using var cap = new ConsoleCapture();
        string dll = Path.Combine(tmp.Path, "vcruntime140.dll");
        File.WriteAllBytes(dll, [0]);
        env.Set("RBMANAGER_VCRUNTIME", dll);

        int rc = await VcRedist.Ensure(assumeYes: false);

        Assert.Equal(0, rc);
        Assert.Contains("vcruntime140.dll is already present", cap.Out);
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("\n")]
    [InlineData("")] // closed stdin: unattended runs decline by default
    public async Task Ensure_Missing_DeclinedOrEof_ReturnsOne(string input)
    {
        using var tmp = new TempDir("vcr");
        using var env = MissingRedist(tmp);
        using var cap = new ConsoleCapture();
        using var stdin = new StdinScope(input);

        int rc = await VcRedist.Ensure(assumeYes: false);

        Assert.Equal(1, rc);
        Assert.Contains("is not installed", cap.Out);
        Assert.Contains("[y/N]", cap.Out);
        Assert.Contains("declined", cap.Err);
    }

    [Fact]
    public void WarnIfMissing_Missing_WarnsToStderr()
    {
        using var tmp = new TempDir("vcr");
        using var env = MissingRedist(tmp);
        using var cap = new ConsoleCapture();

        VcRedist.WarnIfMissing();

        Assert.Equal("", cap.Out);
        Assert.Contains("rb: warning:", cap.Err);
        Assert.Contains("Run `rb setup`", cap.Err);
    }

    [Fact]
    public void WarnIfMissing_Installed_Silent()
    {
        using var key = new ScratchRegistryKey();
        using var env = new EnvScope();
        using var cap = new ConsoleCapture();
        SeedInstalled(key);
        env.Set("RBMANAGER_VC_REDIST_KEY", key.SubKeyPath);

        VcRedist.WarnIfMissing();

        Assert.Equal("", cap.Out);
        Assert.Equal("", cap.Err);
    }

    [Fact]
    public void VerifyMicrosoftSignature_UnsignedFile_Throws()
    {
        using var tmp = new TempDir("vcr");
        string file = Path.Combine(tmp.Path, "unsigned.exe");
        File.WriteAllBytes(file, [0x4D, 0x5A, 1, 2, 3, 4]); // MZ + junk

        var ex = Assert.Throws<InvalidOperationException>(
            () => VcRedist.VerifyMicrosoftSignature(file));

        Assert.Contains("Authenticode", ex.Message);
    }
}

// Opt-in: downloads the real installer and proves the signature gate passes
// on the genuine artifact. Needs the network, so it is skipped unless
// RBMANAGER_TEST_NETWORK=1.
[Trait("Category", "Network")]
public class VcRedistNetworkTests
{
    [SkippableFact]
    public async Task DownloadedInstaller_PassesSignatureVerification()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RBMANAGER_TEST_NETWORK") == "1",
            "set RBMANAGER_TEST_NETWORK=1 to run network tests");

        using var tmp = new TempDir("vcr-net");
        string installer = Path.Combine(tmp.Path, "vc_redist.x64.exe");
        using (var http = new HttpClient())
        await using (var body = await http.GetStreamAsync(
            "https://aka.ms/vs/17/release/vc_redist.x64.exe"))
        await using (var file = File.Create(installer))
        {
            await body.CopyToAsync(file);
        }

        VcRedist.VerifyMicrosoftSignature(installer); // must not throw

        // Flipping one byte in the payload must break the signature.
        byte[] bytes = File.ReadAllBytes(installer);
        bytes[bytes.Length / 2] ^= 0xFF;
        string tampered = Path.Combine(tmp.Path, "tampered.exe");
        File.WriteAllBytes(tampered, bytes);
        Assert.Throws<InvalidOperationException>(
            () => VcRedist.VerifyMicrosoftSignature(tampered));
    }
}
