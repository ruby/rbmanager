using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.5: the dispatch and exit-code contract, and a full lifecycle, driven
// through the built exe.
[Trait("Category", "E2E")]
public class CliE2eTests
{
    private const string V405 = "ruby-4.0.5-x64-mswin64_140";
    private const string V410 = "ruby-4.1.0-x64-mswin64_140";

    private static string[] Lines(string s) =>
        s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact] // case 34
    public void NoArgs_Usage_Exit2()
    {
        using var sb = new E2eSandbox();
        RbResult r = sb.Run();
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("usage: rb <command>", r.Out);
    }

    [Fact] // case 35
    public void UnknownCommand_Usage_Exit2()
    {
        using var sb = new E2eSandbox();
        RbResult r = sb.Run("bogus");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("usage: rb <command>", r.Out);
    }

    [Theory] // case 36
    [InlineData("install")]
    [InlineData("use")]
    [InlineData("msvc")]
    [InlineData("msvc", "--vsver")]
    [InlineData("msvc", "--vsver", "2022")]
    [InlineData("msvc", "--list", "cl")]
    [InlineData("msvc", "enable", "--vsver")]
    [InlineData("msvc", "enable", "cmd", "pwsh")]
    public void MissingRequiredArgument_Usage_Exit2(params string[] command)
    {
        using var sb = new E2eSandbox();
        RbResult r = sb.Run(command);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("usage: rb <command>", r.Out);
    }

    [Fact] // case 37
    public void FailingCommand_ErrorToStderr_Exit1_EmptyStdout()
    {
        using var sb = new E2eSandbox();
        RbResult r = sb.Run("use", "nosuch");
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("", r.Out);
        Assert.StartsWith("rb: ", r.Err);
    }

    [Theory] // cases 88, 118: the flag spellings print the same line
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("-V")]
    public void Version_OneLine_Exit0(string spelling)
    {
        using var sb = new E2eSandbox();
        RbResult r = sb.Run(spelling);

        Assert.Equal(0, r.ExitCode);
        string line = Assert.Single(Lines(r.Out));
        // cargo's shape, e.g. `rbmanager 0.1.0 (9a1b2c3 2026-07-28)`.
        Assert.Matches(@"^rbmanager \d+(\.\d+)+( \([0-9a-f]+ \d{4}-\d{2}-\d{2}\))?$", line);
    }

    [Fact] // case 38
    public void FullLifecycle_ThroughProcessBoundary()
    {
        using var sb = new E2eSandbox();
        string zipA = sb.MakeZip(V405);
        string zipB = sb.MakeZip(V410);

        Assert.Equal(0, sb.Run("install", zipA).ExitCode);
        Assert.Equal([$"* {V405}"], Lines(sb.Run("list").Out));

        Assert.Equal(0, sb.Run("install", zipB).ExitCode);
        Assert.Equal([$"  {V405}", $"* {V410}"], Lines(sb.Run("list").Out));

        Assert.Equal(0, sb.Run("use", "4.0.5").ExitCode);
        Assert.Equal([$"* {V405}", $"  {V410}"], Lines(sb.Run("list").Out));

        Assert.Equal(0, sb.Run("uninstall", "4.1.0").ExitCode);
        Assert.Equal([$"* {V405}"], Lines(sb.Run("list").Out));

        Assert.Equal(0, sb.Run("uninstall", "4.0.5").ExitCode);
        Assert.Equal("", sb.Run("list").Out);
    }
}
