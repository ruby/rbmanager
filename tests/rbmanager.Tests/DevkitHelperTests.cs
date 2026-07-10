namespace RbManager.Tests;

// Plan 4.8: Devkit pure helpers — no VS, no process, no filesystem. Case
// notes pin known limitations (docs/test-plan.md 6.6, 6.7): ParseShell is
// case-sensitive and neither Assignment nor QuoteArg escapes embedded quotes.
[Trait("Category", "Unit")]
public class DevkitHelperTests
{
    [Theory] // case 56
    [InlineData(null)]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("ps")]
    public void ParseShell_DefaultsAndAliases_PowerShell(string? shell) =>
        Assert.Equal(Devkit.Shell.PowerShell, Devkit.ParseShell(shell));

    [Theory] // case 56
    [InlineData("cmd")]
    [InlineData("bat")]
    public void ParseShell_Cmd(string shell) =>
        Assert.Equal(Devkit.Shell.Cmd, Devkit.ParseShell(shell));

    [Fact] // case 56
    public void ParseShell_Unknown_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Devkit.ParseShell("zsh"));
        Assert.Equal("unknown shell 'zsh'", ex.Message);
    }

    [Fact] // case 56 (pin 6.6): ParseShell is case-sensitive
    public void ParseShell_IsCaseSensitive_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Devkit.ParseShell("PowerShell"));

    [Fact] // case 57
    public void Assignment_Cmd()
    {
        Assert.Equal("set \"K=V\"", Devkit.Assignment(Devkit.Shell.Cmd, "K", "V"));
        Assert.Equal("set \"PATH=C:\\a b\\bin\"",
            Devkit.Assignment(Devkit.Shell.Cmd, "PATH", @"C:\a b\bin"));
        Assert.Equal("set \"K=a%B%c\"", Devkit.Assignment(Devkit.Shell.Cmd, "K", "a%B%c"));
        // pin 6.7: embedded quote is not escaped
        Assert.Equal("set \"K=a\"b\"", Devkit.Assignment(Devkit.Shell.Cmd, "K", "a\"b"));
    }

    [Fact] // case 58
    public void Assignment_PowerShell()
    {
        Assert.Equal("$env:K = 'V'", Devkit.Assignment(Devkit.Shell.PowerShell, "K", "V"));
        // single quote doubled
        Assert.Equal("$env:K = 'a''b'", Devkit.Assignment(Devkit.Shell.PowerShell, "K", "a'b"));
        // dollar stays literal (single-quoted PowerShell literal)
        Assert.Equal("$env:K = 'a$b'", Devkit.Assignment(Devkit.Shell.PowerShell, "K", "a$b"));
    }

    [Fact] // case 59
    public void Unset_BothShells()
    {
        Assert.Equal("set \"K=\"", Devkit.Unset(Devkit.Shell.Cmd, "K"));
        Assert.Equal("Remove-Item Env:\\K -ErrorAction SilentlyContinue",
            Devkit.Unset(Devkit.Shell.PowerShell, "K"));
    }

    [Fact] // case 60
    public void QuoteArg()
    {
        Assert.Equal("gem", Devkit.QuoteArg("gem"));
        Assert.Equal("\"a b\"", Devkit.QuoteArg("a b"));
        Assert.Equal("\"\"", Devkit.QuoteArg(""));
        Assert.Equal("\"\t\"", Devkit.QuoteArg("\t"));
        // pin 6.7: embedded quote is not escaped, only wrapped when needed
        Assert.Equal("a\"b", Devkit.QuoteArg("a\"b"));
    }
}
