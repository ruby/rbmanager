using RbManager;

namespace RbManager.Tests;

// Phase 0 smoke test: proves the test project references the product and
// that InternalsVisibleTo grants access to internal members. Reaching a
// non-public member (ParseShell / the Shell enum) is the point; the actual
// behavior suites replace this in later phases.
[Trait("Category", "Unit")]
public class SmokeTests
{
    [Fact]
    public void InternalsAreVisible()
    {
        Assert.Equal(Msvc.Shell.Cmd, Msvc.ParseShell("cmd"));
        Assert.Equal(Msvc.Shell.PowerShell, Msvc.ParseShell(null));
    }
}
