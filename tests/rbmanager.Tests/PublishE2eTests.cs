using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.11 (+ relocated case 32): drive the published AOT single-file exe.
// This is the one place the AOT binary is exercised, so it verifies the
// embedded trust hook and the LibraryImport P/Invokes survive AOT, and that
// the self-copy path works on a genuinely self-contained exe. Opt-in and slow
// (a full publish); skips when the publish is unavailable.
[Trait("Category", "Publish")]
public class PublishE2eTests : IClassFixture<PublishFixture>
{
    private readonly PublishFixture _fx;

    public PublishE2eTests(PublishFixture fx) => _fx = fx;

    [SkippableFact] // case 75
    public void Install_UnderAot_WritesTrustHookAndSwitches()
    {
        Skip.If(_fx.ExePath is null, _fx.SkipReason);
        using var sb = new E2eSandbox();
        const string name = "ruby-9.9.9-x64-mswin64_140";
        string zip = sb.MakeZip(name);

        RbResult r = sb.RunExe(_fx.ExePath!, "install", zip);

        Assert.Equal(0, r.ExitCode);
        Assert.True(Directory.Exists(Path.Combine(sb.Rubies, name)));
        string hook = Path.Combine(sb.Rubies, name,
            "lib", "ruby", "site_ruby", "rubygems", "defaults", "operating_system.rb");
        Assert.True(File.Exists(hook));
    }

    // Compared against the in-process resolution rather than a literal.
    // Both come from the same source tree at the same commit, so any
    // divergence is AOT dropping the assembly metadata `rb version` reads.
    [SkippableFact] // case 93
    public void Version_UnderAot_MatchesTheInProcessResolution()
    {
        Skip.If(_fx.ExePath is null, _fx.SkipReason);
        using var sb = new E2eSandbox();

        RbResult r = sb.RunExe(_fx.ExePath!, "version");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(Program.SelfVersion(), r.Out.Trim());
    }

    [SkippableFact] // case 32: self-copy skip works on the self-contained exe
    public void Setup_FromCopiedExe_NoSelfCopyError()
    {
        Skip.If(_fx.ExePath is null, _fx.SkipReason);
        using var sb = new E2eSandbox();
        sb.RunExe(_fx.ExePath!, "setup");
        string copied = Path.Combine(sb.Root, "bin", "rb.exe");

        RbResult r = sb.RunExe(copied, "setup");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Installed rb to", r.Out);
    }
}
