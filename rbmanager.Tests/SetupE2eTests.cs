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
}
