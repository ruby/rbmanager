using Microsoft.Win32;
using RbManager.Tests.Support;

namespace RbManager.Tests;

// VC++ Redistributable detection and the rb setup consent flow. Registry
// state is simulated through RBMANAGER_VC_REDIST_KEY (an HKCU scratch
// subkey standing in for the Runtimes key), the DLL fallback through
// RBMANAGER_VCRUNTIME and the host through RBMANAGER_OS_ARCH, so nothing
// here reads the host machine's real installation state. The install path
// itself (download, signature check, elevation) is not driven end to end;
// the signature verifier is covered by the unsigned-rejection case here
// and by the opt-in Network test against the real installer.
[Trait("Category", "Unit")]
[Collection(Serial.Name)]
public class VcRedistTests
{
    private const string Version = "v14.99.1.0";

    private static void SeedInstalled(ScratchRegistryKey key, int installed = 1,
        string runtime = "x64")
    {
        using RegistryKey sub = key.Key.CreateSubKey(runtime);
        sub.SetValue("Installed", installed, RegistryValueKind.DWord);
        sub.SetValue("Version", Version);
    }

    // The seams at the scratch key on a host of the given architecture.
    private static EnvScope Redist(ScratchRegistryKey key, TempDir tmp, string arch = "X64")
    {
        var env = new EnvScope();
        env.Set("RBMANAGER_VC_REDIST_KEY", key.SubKeyPath);
        env.Set("RBMANAGER_VCRUNTIME", Path.Combine(tmp.Path, "no-vcruntime140.dll"));
        env.Set("RBMANAGER_OS_ARCH", arch);
        return env;
    }

    // Points both seams at empty/nonexistent targets: a machine without the
    // redistributable. Returns the scope; dispose to restore.
    private static EnvScope MissingRedist(TempDir tmp)
    {
        var env = new EnvScope();
        env.Set("RBMANAGER_VC_REDIST_KEY",
            $@"Software\rbmanager-tests\{Guid.NewGuid():N}");
        env.Set("RBMANAGER_VCRUNTIME", Path.Combine(tmp.Path, "no-vcruntime140.dll"));
        env.Set("RBMANAGER_OS_ARCH", "X64");
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

        Assert.Equal(Version, VcRedist.InstalledVersion("x64"));
        Assert.Null(VcRedist.InstalledVersion("ARM64"));
    }

    [Fact]
    public void InstalledVersion_InstalledZero_ReturnsNull()
    {
        using var key = new ScratchRegistryKey();
        using var env = new EnvScope();
        SeedInstalled(key, installed: 0);
        env.Set("RBMANAGER_VC_REDIST_KEY", key.SubKeyPath);

        Assert.Null(VcRedist.InstalledVersion("x64"));
    }

    [Fact]
    public void InstalledVersion_MissingKey_ReturnsNull()
    {
        using var env = new EnvScope();
        env.Set("RBMANAGER_VC_REDIST_KEY",
            $@"Software\rbmanager-tests\{Guid.NewGuid():N}");

        Assert.Null(VcRedist.InstalledVersion("x64"));
    }

    [Fact]
    public void InstalledVersion_NoVersionValue_ReturnsPlaceholder()
    {
        using var key = new ScratchRegistryKey();
        using var env = new EnvScope();
        using (RegistryKey sub = key.Key.CreateSubKey("x64"))
            sub.SetValue("Installed", 1, RegistryValueKind.DWord);
        env.Set("RBMANAGER_VC_REDIST_KEY", key.SubKeyPath);

        Assert.Equal("(unknown version)", VcRedist.InstalledVersion("x64"));
    }

    [Fact]
    public void IsInstalled_RegistryMissing_FallsBackToDll()
    {
        using var tmp = new TempDir("vcr");
        using var env = MissingRedist(tmp);
        Assert.False(VcRedist.IsInstalled("x64"));

        string dll = Path.Combine(tmp.Path, "vcruntime140.dll");
        File.WriteAllBytes(dll, [0]);
        env.Set("RBMANAGER_VCRUNTIME", dll);
        Assert.True(VcRedist.IsInstalled("x64"));
    }

    [Theory] // case 140: arm64 rubies need the ARM64 runtime, the rest x64
    [InlineData("ruby-3.4.12-arm64-mswin64_140", "ARM64")]
    [InlineData(BinaryIndex.Arm64, "ARM64")]
    [InlineData("ruby-3.4.12-x64-mswin64_140", "x64")]
    [InlineData(BinaryIndex.X64, "x64")]
    [InlineData("ruby-3.4.12-x86-mswin64_140", "x64")]
    public void RuntimeOf_MapsPlatformToRuntimesKey(string ruby, string runtime) =>
        Assert.Equal(runtime, VcRedist.RuntimeOf(ruby));

    [Fact]
    public async Task Ensure_AlreadyInstalled_ReportsAndReturnsZero()
    {
        using var key = new ScratchRegistryKey();
        using var tmp = new TempDir("vcr");
        using var env = Redist(key, tmp);
        using var cap = new ConsoleCapture();
        SeedInstalled(key);

        int rc = await VcRedist.Ensure(assumeYes: false);

        Assert.Equal(0, rc);
        Assert.Contains($"VC++ Redistributable {Version} is already installed", cap.Out);
    }

    [Fact] // case 141: on ARM64 setup needs both runtimes
    public async Task Ensure_Arm64Host_BothRuntimes_ReturnsZero()
    {
        using var key = new ScratchRegistryKey();
        using var tmp = new TempDir("vcr");
        using var env = Redist(key, tmp, "Arm64");
        using var cap = new ConsoleCapture();
        SeedInstalled(key);
        SeedInstalled(key, runtime: "ARM64");

        Assert.Equal(0, await VcRedist.Ensure(assumeYes: false));
        Assert.Contains("is already installed", cap.Out);
    }

    [Fact] // case 142: an x64-only runtime on ARM64 offers the x64 package
    public async Task Ensure_Arm64Host_MissingArm64Runtime_Offers()
    {
        using var key = new ScratchRegistryKey();
        using var tmp = new TempDir("vcr");
        using var env = Redist(key, tmp, "Arm64");
        using var cap = new ConsoleCapture();
        using var stdin = new StdinScope("");
        SeedInstalled(key);

        Assert.Equal(1, await VcRedist.Ensure(assumeYes: false));
        Assert.Contains("Visual C++ Redistributable (ARM64) is not installed", cap.Out);
        Assert.Contains("vc_redist.x64.exe", cap.Out);
    }

    [Fact] // case 149: on ARM64 the DLL does not stand in for the ARM64 runtime
    public async Task Ensure_Arm64Host_DllPresent_StillOffersArm64()
    {
        using var key = new ScratchRegistryKey();
        using var tmp = new TempDir("vcr");
        using var env = Redist(key, tmp, "Arm64");
        using var cap = new ConsoleCapture();
        using var stdin = new StdinScope("");
        SeedInstalled(key);
        string dll = Path.Combine(tmp.Path, "vcruntime140.dll");
        File.WriteAllBytes(dll, [0]);
        env.Set("RBMANAGER_VCRUNTIME", dll);

        Assert.Equal(1, await VcRedist.Ensure(assumeYes: false));
        Assert.Contains("Visual C++ Redistributable (ARM64) is not installed", cap.Out);
        Assert.DoesNotContain("already present", cap.Out);
    }

    [Fact] // case 150: nor for either runtime of a ruby installed or used there
    public void WarnIfMissing_Arm64Host_DllPresent_StillWarns()
    {
        using var key = new ScratchRegistryKey();
        using var tmp = new TempDir("vcr");
        using var env = Redist(key, tmp, "Arm64");
        using var cap = new ConsoleCapture();
        string dll = Path.Combine(tmp.Path, "vcruntime140.dll");
        File.WriteAllBytes(dll, [0]);
        env.Set("RBMANAGER_VCRUNTIME", dll);

        Assert.False(VcRedist.IsInstalled("ARM64"));
        Assert.False(VcRedist.IsInstalled("x64"));
        VcRedist.WarnIfMissing("ruby-9.9.9-arm64-mswin64_140");
        Assert.Contains("Visual C++ Redistributable (ARM64) was not found", cap.Err);
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

        VcRedist.WarnIfMissing("ruby-9.9.9-x64-mswin64_140");

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

        VcRedist.WarnIfMissing("ruby-9.9.9-x64-mswin64_140");

        Assert.Equal("", cap.Out);
        Assert.Equal("", cap.Err);
    }

    [Fact] // case 143: an arm64 ruby is checked against the ARM64 runtime
    public void WarnIfMissing_Arm64Ruby_ChecksArm64Runtime()
    {
        using var key = new ScratchRegistryKey();
        using var tmp = new TempDir("vcr");
        using var env = Redist(key, tmp, "Arm64");
        using var cap = new ConsoleCapture();
        SeedInstalled(key);

        VcRedist.WarnIfMissing("ruby-9.9.9-x64-mswin64_140");
        Assert.Equal("", cap.Err);

        VcRedist.WarnIfMissing("ruby-9.9.9-arm64-mswin64_140");
        Assert.Contains("Visual C++ Redistributable (ARM64) was not found", cap.Err);
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
