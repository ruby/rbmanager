using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.10: Devkit against a real Visual Studio install. Opt-in: each test
// skips (rather than fails) when no MSVC toolchain is discoverable, so the
// suite is safe to run anywhere but only asserts on a VS machine.
[Trait("Category", "RequiresVS")]
public class DevkitVsTests
{
    private static string? RealVsDevCmd() => Devkit.LocateVsDevCmd();

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

        var delta = Devkit.ActivatedDelta(bat!)
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

        RbResult r = sb.Run("exec", "cl");

        // cl with no input files prints its version banner to stderr.
        Assert.Contains("Microsoft", r.Err);
    }
}
