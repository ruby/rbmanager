using System.Runtime.InteropServices;
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

    // The fixture publishes win-x64, so on an ARM64 machine this runs the
    // exe under emulation, where OSArchitecture must still report Arm64.
    [SkippableFact] // case 144
    public void ListRemote_UnderAot_SeesTheMachineArchitecture()
    {
        Skip.If(_fx.ExePath is null, _fx.SkipReason);
        using var sb = new E2eSandbox();
        sb.UseHostArchitecture();
        string index = Path.Combine(sb.Root, "_index", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        File.WriteAllText(index, $$"""
            {"schema": 1, "next": null,
             "builds": [{{Entry(BinaryIndex.Arm64)}}, {{Entry(BinaryIndex.X64)}}]}
            """);
        sb.UseIndex(index);

        RbResult r = sb.RunExe(_fx.ExePath!, "list", "--remote");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"ruby-4.0.9-{BinaryIndex.X64}", r.Out);
        Assert.Equal(RuntimeInformation.OSArchitecture == Architecture.Arm64,
            r.Out.Contains($"ruby-4.0.9-{BinaryIndex.Arm64}"));

        static string Entry(string platform) => $$"""
            {"name": "ruby-4.0.9-{{platform}}", "version": "4.0.9", "channel": "release",
             "revision": 0, "tags": ["4.0.9"], "platform": "{{platform}}",
             "url": "https://example.invalid/x.zip", "sha256": "{{new string('0', 64)}}",
             "size": 1, "commit": null, "commit_date": null, "published_at": null,
             "signed": false}
            """;
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
