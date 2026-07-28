using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.10: Msvc against a real Visual Studio install. Opt-in: each test
// skips (rather than fails) when no MSVC toolchain is discoverable, so the
// suite is safe to run anywhere but only asserts on a VS machine.
[Trait("Category", "RequiresVS")]
public class MsvcVsTests
{
    private static string? RealVsDevCmd() => Msvc.LocateVsDevCmd();

    [SkippableFact] // case 72
    public void LocateVsDevCmd_ReturnsExistingBat()
    {
        string? bat = RealVsDevCmd();
        Skip.If(bat is null, "no Visual Studio C++ toolchain installed");
        Assert.True(File.Exists(bat));
    }

    [SkippableFact] // case 73
    public void ActivatedDelta_IncludesCompilerEnvironment()
    {
        string? bat = RealVsDevCmd();
        Skip.If(bat is null, "no Visual Studio C++ toolchain installed");

        var delta = Msvc.ActivatedDelta(bat!)
            .ToDictionary(t => t.Item1, t => t.Item2, StringComparer.OrdinalIgnoreCase);

        Assert.True(delta.ContainsKey("INCLUDE"));
        Assert.True(delta.ContainsKey("LIB"));
        Assert.True(delta.ContainsKey("PATH"));
    }

    [SkippableFact] // case 74
    public void Exec_Cl_RunsCompiler()
    {
        Skip.If(RealVsDevCmd() is null, "no Visual Studio C++ toolchain installed");
        using var sb = new E2eSandbox();

        RbResult r = sb.Run("msvc", "cl");

        // cl with no input files prints its version banner to stderr.
        Assert.Contains("Microsoft", r.Err);
    }

    [SkippableFact] // case 83: --vsver resolves the newest install of that year
    public void LocateVsDevCmd_PerYear_ResolvesMatchingInstall()
    {
        var installs = Msvc.Installs();
        Skip.If(installs.Count == 0, "no Visual Studio C++ toolchain installed");

        foreach (var expected in installs.GroupBy(i => i.Year).Select(g => g.First()))
        {
            Skip.If(!Msvc.VsVerRanges.ContainsKey(expected.Year),
                $"unmapped product year {expected.Year}");
            string? bat = Msvc.LocateVsDevCmd(expected.Year);
            Assert.NotNull(bat);
            Assert.StartsWith(expected.Path, bat, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact] // case 84
    public void List_MarksDefaultAndPrintsYears()
    {
        var installs = Msvc.Installs();
        Skip.If(installs.Count == 0, "no Visual Studio C++ toolchain installed");
        using var sb = new E2eSandbox();

        RbResult r = sb.Run("msvc", "--list");

        Assert.Equal(0, r.ExitCode);
        string[] lines = r.Out.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal(installs.Count, lines.Length);
        // newest first carries the default marker; every line names its year
        Assert.StartsWith($"* {installs[0].Year}", lines[0]);
        foreach ((string line, var install) in lines.Zip(installs))
            Assert.Contains(install.Path, line);
    }

    [SkippableFact] // case 85: the passthrough with an installed year still finds cl
    public void Exec_WithVsVer_RunsCompiler()
    {
        var installs = Msvc.Installs();
        Skip.If(installs.Count == 0, "no Visual Studio C++ toolchain installed");
        string year = installs[0].Year;
        Skip.If(!Msvc.VsVerRanges.ContainsKey(year), $"unmapped product year {year}");
        using var sb = new E2eSandbox();

        RbResult r = sb.Run("msvc", "--vsver", year, "cl");

        Assert.Contains("Microsoft", r.Err);
    }
}
