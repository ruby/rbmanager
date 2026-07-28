namespace RbManager.Tests;

// Plan 4.8: Msvc pure helpers — no VS, no process, no filesystem. Case
// notes pin known limitations (docs/test-plan.md 6.6, 6.7): ParseShell is
// case-sensitive and neither Assignment nor QuoteArg escapes embedded quotes.
[Trait("Category", "Unit")]
public class MsvcHelperTests
{
    [Theory] // case 56
    [InlineData(null)]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("ps")]
    public void ParseShell_DefaultsAndAliases_PowerShell(string? shell) =>
        Assert.Equal(Msvc.Shell.PowerShell, Msvc.ParseShell(shell));

    [Theory] // case 56
    [InlineData("cmd")]
    [InlineData("bat")]
    public void ParseShell_Cmd(string shell) =>
        Assert.Equal(Msvc.Shell.Cmd, Msvc.ParseShell(shell));

    [Fact] // case 56
    public void ParseShell_Unknown_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Msvc.ParseShell("zsh"));
        Assert.Equal("unknown shell 'zsh'", ex.Message);
    }

    [Fact] // case 56 (pin 6.6): ParseShell is case-sensitive
    public void ParseShell_IsCaseSensitive_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Msvc.ParseShell("PowerShell"));

    [Fact] // case 57
    public void Assignment_Cmd()
    {
        Assert.Equal("set \"K=V\"", Msvc.Assignment(Msvc.Shell.Cmd, "K", "V"));
        Assert.Equal("set \"PATH=C:\\a b\\bin\"",
            Msvc.Assignment(Msvc.Shell.Cmd, "PATH", @"C:\a b\bin"));
        Assert.Equal("set \"K=a%B%c\"", Msvc.Assignment(Msvc.Shell.Cmd, "K", "a%B%c"));
        // pin 6.7: embedded quote is not escaped
        Assert.Equal("set \"K=a\"b\"", Msvc.Assignment(Msvc.Shell.Cmd, "K", "a\"b"));
    }

    [Fact] // case 58
    public void Assignment_PowerShell()
    {
        Assert.Equal("$env:K = 'V'", Msvc.Assignment(Msvc.Shell.PowerShell, "K", "V"));
        // single quote doubled
        Assert.Equal("$env:K = 'a''b'", Msvc.Assignment(Msvc.Shell.PowerShell, "K", "a'b"));
        // dollar stays literal (single-quoted PowerShell literal)
        Assert.Equal("$env:K = 'a$b'", Msvc.Assignment(Msvc.Shell.PowerShell, "K", "a$b"));
    }

    [Fact] // case 59
    public void Unset_BothShells()
    {
        Assert.Equal("set \"K=\"", Msvc.Unset(Msvc.Shell.Cmd, "K"));
        Assert.Equal("Remove-Item Env:\\K -ErrorAction SilentlyContinue",
            Msvc.Unset(Msvc.Shell.PowerShell, "K"));
    }

    [Fact] // case 60
    public void QuoteArg()
    {
        Assert.Equal("gem", Msvc.QuoteArg("gem"));
        Assert.Equal("\"a b\"", Msvc.QuoteArg("a b"));
        Assert.Equal("\"\"", Msvc.QuoteArg(""));
        Assert.Equal("\"\t\"", Msvc.QuoteArg("\t"));
        // pin 6.7: embedded quote is not escaped, only wrapped when needed
        Assert.Equal("a\"b", Msvc.QuoteArg("a\"b"));
    }

    private static void AssertEnable(string? shell, string? vsver, params string[] args)
    {
        var parsed = Msvc.EnableArgs(args);
        Assert.NotNull(parsed);
        Assert.Equal(shell, parsed.Value.Shell);
        Assert.Equal(vsver, parsed.Value.VsVer);
    }

    private static void AssertExec(string[] command, string? vsver, params string[] args)
    {
        var parsed = Msvc.ExecArgs(args);
        Assert.NotNull(parsed);
        Assert.Equal(command, parsed.Value.Command);
        Assert.Equal(vsver, parsed.Value.VsVer);
    }

    [Fact] // case 76
    public void EnableArgs_ShellAndVsVer_EitherOrder()
    {
        AssertEnable(null, null);
        AssertEnable("cmd", null, "cmd");
        AssertEnable(null, "2019", "--vsver", "2019");
        AssertEnable("cmd", "2019", "--vsver", "2019", "cmd");
        AssertEnable("cmd", "2019", "cmd", "--vsver=2019");
    }

    [Fact] // case 76
    public void EnableArgs_Malformed_Null()
    {
        Assert.Null(Msvc.EnableArgs(["--vsver"]));       // missing value
        Assert.Null(Msvc.EnableArgs(["--vsver="]));      // empty value
        Assert.Null(Msvc.EnableArgs(["--bogus"]));       // unknown option
        Assert.Null(Msvc.EnableArgs(["cmd", "pwsh"]));   // two shells
    }

    [Fact] // case 77
    public void ExecArgs_LeadingOptionsThenCommand()
    {
        AssertExec(["gem", "install", "json"], null, "gem", "install", "json");
        AssertExec(["cl"], "2019", "--vsver", "2019", "cl");
        AssertExec(["cl"], "2019", "--vsver=2019", "cl");
        AssertExec(["cl"], "2019", "--vsver", "2019", "--", "cl");
    }

    [Fact] // case 77: after `--` everything is command, never options
    public void ExecArgs_DoubleDash_PassesOptionsThrough() =>
        AssertExec(["--vsver", "2019"], null, "--", "--vsver", "2019");

    [Fact] // case 77: options past the first command token stay untouched
    public void ExecArgs_OptionAfterCommand_IsCommand() =>
        AssertExec(["ruby", "--vsver", "x"], null, "ruby", "--vsver", "x");

    [Fact] // case 77
    public void ExecArgs_Malformed_Null()
    {
        Assert.Null(Msvc.ExecArgs([]));                    // no command
        Assert.Null(Msvc.ExecArgs(["--vsver"]));           // missing value
        Assert.Null(Msvc.ExecArgs(["--vsver", "2019"]));   // option but no command
        Assert.Null(Msvc.ExecArgs(["--vsver=", "cl"]));    // empty value
        Assert.Null(Msvc.ExecArgs(["--bogus", "cl"]));     // unknown option
        Assert.Null(Msvc.ExecArgs(["--"]));                // separator alone
    }

    private static void AssertParsedRun(string[] command, string? vsver, params string[] args)
    {
        var parsed = Msvc.Parse(args);
        Assert.NotNull(parsed);
        Assert.Equal(Msvc.Op.Run, parsed.Value.Op);
        Assert.Equal(command, parsed.Value.Command);
        Assert.Equal(vsver, parsed.Value.VsVer);
    }

    private static void AssertParsedEnable(string? shell, string? vsver, params string[] args)
    {
        var parsed = Msvc.Parse(args);
        Assert.NotNull(parsed);
        Assert.Equal(Msvc.Op.Enable, parsed.Value.Op);
        Assert.Equal(shell, parsed.Value.Shell);
        Assert.Equal(vsver, parsed.Value.VsVer);
    }

    [Fact] // case 86: a bare command is the user's, passed through verbatim
    public void Parse_Command_PassesThrough()
    {
        AssertParsedRun(["gem", "install", "nokogiri"], null, "gem", "install", "nokogiri");
        AssertParsedRun(["gem", "install", "nokogiri"], "2022",
            "--vsver", "2022", "gem", "install", "nokogiri");
        AssertParsedRun(["cl"], "2022", "--vsver=2022", "cl");
    }

    [Fact] // case 86: `enable` is the one reserved word, --vsver on either side
    public void Parse_Enable_ReachesEnable()
    {
        AssertParsedEnable(null, null, "enable");
        AssertParsedEnable("cmd", null, "enable", "cmd");
        AssertParsedEnable("powershell", "2022", "enable", "--vsver", "2022", "powershell");
        AssertParsedEnable("powershell", "2022", "--vsver", "2022", "enable", "powershell");
    }

    [Fact] // case 86: `--` makes even the reserved word a command
    public void Parse_DoubleDash_EnableIsCommand()
    {
        AssertParsedRun(["enable"], null, "--", "enable");
        AssertParsedRun(["enable", "cmd"], "2022", "--vsver", "2022", "--", "enable", "cmd");
    }

    [Fact] // case 86: --list is terminal
    public void Parse_List()
    {
        var parsed = Msvc.Parse(["--list"]);
        Assert.NotNull(parsed);
        Assert.Equal(Msvc.Op.List, parsed.Value.Op);
        // a year does not apply to a query, so it is read but unused
        Assert.Equal(Msvc.Op.List, Msvc.Parse(["--vsver", "2022", "--list"])!.Value.Op);

        Assert.Null(Msvc.Parse(["--list", "cl"]));       // command after the query
        Assert.Null(Msvc.Parse(["--list", "enable"]));   // reserved word too
    }

    [Fact] // case 86: option recognition stops at the first command token
    public void Parse_OptionsAreLeadingOnly()
    {
        AssertParsedRun(["ruby", "--list"], null, "ruby", "--list");
        AssertParsedRun(["ruby", "--vsver", "2022"], null, "ruby", "--vsver", "2022");
    }

    [Fact] // case 86
    public void Parse_Malformed_Null()
    {
        Assert.Null(Msvc.Parse([]));                     // bare `rb msvc`
        Assert.Null(Msvc.Parse(["--vsver"]));            // missing value
        Assert.Null(Msvc.Parse(["--vsver", "2022"]));    // option but no command
        Assert.Null(Msvc.Parse(["--vsver=", "cl"]));     // empty value
        Assert.Null(Msvc.Parse(["--bogus", "cl"]));      // unknown leading option
        Assert.Null(Msvc.Parse(["--"]));                 // separator alone
        Assert.Null(Msvc.Parse(["enable", "cmd", "pwsh"]));  // two shells
    }

    [Fact] // case 78: the year map covers exactly the VsDevCmd-era products
    public void VsVerRanges_YearToInstallationVersionRange()
    {
        Assert.Equal("[15.0,16.0)", Msvc.VsVerRanges["2017"]);
        Assert.Equal("[16.0,17.0)", Msvc.VsVerRanges["2019"]);
        Assert.Equal("[17.0,18.0)", Msvc.VsVerRanges["2022"]);
        Assert.Equal("[18.0,19.0)", Msvc.VsVerRanges["2026"]);
        Assert.Equal(4, Msvc.VsVerRanges.Count);
    }
}
