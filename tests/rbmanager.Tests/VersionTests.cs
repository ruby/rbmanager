namespace RbManager.Tests;

// Plan 4.12: the line behind `rb version`, in cargo's shape. The values come
// from whatever the build stamped into the assembly, so these pin the
// formatting and the degraded cases rather than a literal.
[Trait("Category", "Unit")]
public class VersionTests
{
    [Fact] // case 89
    public void Format_VersionCommitAndDate()
    {
        Assert.Equal("rbmanager 0.1.0 (9a1b2c3 2026-07-28)",
            Program.FormatVersion("0.1.0+9a1b2c3", "0.1.0.0", "2026-07-28"));
    }

    [Theory] // case 90
    // No git checkout to ask: no suffix, no date, so no parenthetical.
    [InlineData("0.1.0", null, "rbmanager 0.1.0")]
    // The date is a separate attribute, so either half can go missing.
    [InlineData("0.1.0+9a1b2c3", null, "rbmanager 0.1.0 (9a1b2c3)")]
    [InlineData("0.1.0", "2026-07-28", "rbmanager 0.1.0 (2026-07-28)")]
    // A prerelease tag keeps its own hyphenated suffix.
    [InlineData("1.2.3-rc1+9a1b2c3", "2026-07-28", "rbmanager 1.2.3-rc1 (9a1b2c3 2026-07-28)")]
    public void Format_PartialStamps(string? informational, string? date, string expected) =>
        Assert.Equal(expected, Program.FormatVersion(informational, "0.1.0.0", date));

    [Theory] // case 91
    // Without the informational version the assembly version stands in, and
    // with neither there is still a line to print.
    [InlineData(null, "0.1.0.0", "rbmanager 0.1.0.0")]
    [InlineData("", null, "rbmanager unknown")]
    public void Format_FallsBackToTheAssemblyVersion(
        string? informational, string? assembly, string expected) =>
        Assert.Equal(expected, Program.FormatVersion(informational, assembly, null));

    [Fact] // case 92
    public void SelfVersion_ReportsThisBuild()
    {
        string line = Program.SelfVersion();

        Assert.StartsWith("rbmanager ", line);
        Assert.True(Version.TryParse(line.Split(' ')[1], out _), line);
    }
}
