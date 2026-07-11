using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.4: rb setup driven end to end through the built exe. Redirection is
// passed to the child, so the real PATH and %LOCALAPPDATA% stay untouched.
[Trait("Category", "E2E")]
public class SetupE2eTests
{
    [Fact] // case 31
    public void Setup_CopiesSelfOntoPathAndEnsuresPath()
    {
        using var sb = new E2eSandbox();

        RbResult r = sb.Run("setup");

        Assert.Equal(0, r.ExitCode);
        string dest = Path.Combine(sb.Root, "bin", "rb.exe");
        Assert.True(File.Exists(dest));
        Assert.Equal(File.ReadAllBytes(Rb.Exe), File.ReadAllBytes(dest));
        Assert.Contains(Path.Combine(sb.Root, "bin"), sb.WrittenPath());
        Assert.Contains("Installed rb to", r.Out);
        Assert.Contains("Open a new terminal", r.Out);
    }

    // case 32 (setup run from the copied exe) requires the self-contained
    // single-file artifact; the framework-dependent Debug apphost cannot run
    // without rb.dll beside it. It lives in PublishE2eTests (opt-in).

    [Fact] // case 33: an existing bin\rb.exe is overwritten
    public void Setup_OverwritesExistingBinExe()
    {
        using var sb = new E2eSandbox();
        string dest = Path.Combine(sb.Root, "bin", "rb.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, [0, 0, 0]); // stale content

        RbResult r = sb.Run("setup");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(File.ReadAllBytes(Rb.Exe), File.ReadAllBytes(dest));
    }

    [Fact] // setup reports the (sandbox-seeded) VC++ runtime as present
    public void Setup_VcRedistInstalled_ReportsAlreadyInstalled()
    {
        using var sb = new E2eSandbox();

        RbResult r = sb.Run("setup");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("VC++ Redistributable v14.99.0.0 is already installed", r.Out);
    }

    [Fact] // --yes is accepted (and a no-op when already installed)
    public void Setup_YesFlag_Accepted()
    {
        using var sb = new E2eSandbox();

        RbResult r = sb.Run("setup", "--yes");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("already installed", r.Out);
    }

    // The install path (download + elevation) is never driven from tests;
    // with the runtime missing and stdin closed the prompt reads EOF, which
    // declines before anything touches the network.
    [Fact]
    public void Setup_VcRedistMissing_ClosedStdinDeclines_ExitsOne()
    {
        using var sb = new E2eSandbox();
        sb.RemoveVcRedist();

        RbResult r = sb.Run("setup");

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("is not installed", r.Out);
        Assert.Contains("declined", r.Err);
        // self-installation still completed before the runtime check
        Assert.True(File.Exists(Path.Combine(sb.Root, "bin", "rb.exe")));
    }

    [Fact] // install and use warn (but succeed) when the runtime is missing
    public void InstallAndUse_VcRedistMissing_WarnButSucceed()
    {
        using var sb = new E2eSandbox();
        sb.RemoveVcRedist();
        string zip = sb.MakeZip("ruby-9.9.9-x64-mswin64_140");

        RbResult install = sb.Run("install", zip);
        Assert.Equal(0, install.ExitCode);
        Assert.Contains("Run `rb setup`", install.Err);

        RbResult use = sb.Run("use", "9.9.9");
        Assert.Equal(0, use.ExitCode);
        Assert.Contains("Run `rb setup`", use.Err);
    }

    [Fact] // no warning when the runtime is present
    public void Install_VcRedistInstalled_NoWarning()
    {
        using var sb = new E2eSandbox();
        string zip = sb.MakeZip("ruby-9.9.9-x64-mswin64_140");

        RbResult r = sb.Run("install", zip);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Err);
    }
}
