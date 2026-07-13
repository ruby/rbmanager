using RbManager.Tests.Support;

namespace RbManager.Tests;

// Plan 4.9: Msvc activation exercised with a stub VsDevCmd.bat, so no real
// Visual Studio is needed. Serial (env vars, console, the Msvc.VsWhere
// static). The stub is invoked by ActivatedDelta as
// `cmd /s /c "call "stub" -arch=... -no_logo && set"`.
[Trait("Category", "Integration")]
[Collection(Serial.Name)]
public class MsvcActivationTests
{
    private const string NoDefault = "NoDefaultCurrentDirectoryInExePath";
    private const string PwshUnset =
        "Remove-Item Env:\\NoDefaultCurrentDirectoryInExePath -ErrorAction SilentlyContinue";
    private const string CmdUnset = "set \"NoDefaultCurrentDirectoryInExePath=\"";

    private static string Bat(TempDir tmp, params string[] lines)
    {
        string path = tmp.At($"bat-{Guid.NewGuid():N}.bat");
        File.WriteAllText(path, "@echo off\r\n" + string.Join("\r\n", lines) + "\r\n");
        return path;
    }

    private static Dictionary<string, string> Delta(string bat) =>
        Msvc.ActivatedDelta(bat)
            .ToDictionary(t => t.Item1, t => t.Item2, StringComparer.OrdinalIgnoreCase);

    [Fact] // case 61
    public void ActivatedDelta_ReportsAddedAndChangedOnly()
    {
        using var tmp = new TempDir();
        string bat = Bat(tmp, "set RB_TEST_NEW=hello", @"set PATH=C:\rbstub;%PATH%");

        var delta = Delta(bat);

        Assert.Equal("hello", delta["RB_TEST_NEW"]);
        Assert.True(delta.ContainsKey("PATH"));
        Assert.False(delta.ContainsKey("SystemRoot")); // inherited, unchanged
    }

    [Fact] // case 62
    public void ActivatedDelta_DetectsChangedPreexistingVar()
    {
        using var tmp = new TempDir();
        using var env = new EnvScope();
        env.Set("RB_PRE", "old");
        string bat = Bat(tmp, "set RB_PRE=new");

        Assert.Equal("new", Delta(bat)["RB_PRE"]);
    }

    [Fact] // case 63
    public void ActivatedDelta_SplitsOnFirstEqualsOnly()
    {
        using var tmp = new TempDir();
        string bat = Bat(tmp, "set RB_EQ=a=b=c");

        Assert.Equal("a=b=c", Delta(bat)["RB_EQ"]);
    }

    [Fact] // case 64
    public void ActivatedDelta_NonZeroExit_Throws()
    {
        using var tmp = new TempDir();
        string bat = Bat(tmp, "exit /b 3");

        var ex = Assert.Throws<InvalidOperationException>(
            () => Msvc.ActivatedDelta(bat).ToList());
        Assert.Equal("VsDevCmd activation failed", ex.Message);
    }

    [Fact] // case 65 (PowerShell)
    public void Enable_PowerShell_AssignmentsThenUnset()
    {
        using var tmp = new TempDir();
        using var env = new EnvScope();
        env.Set("RBMANAGER_VSDEVCMD", Bat(tmp, "set RB_TEST_NEW=hello"));
        using var cap = new ConsoleCapture();

        int rc = Msvc.Enable("powershell");

        Assert.Equal(0, rc);
        Assert.Contains("$env:RB_TEST_NEW = 'hello'", cap.OutLines);
        Assert.Equal(PwshUnset, cap.OutLines[^1]);
    }

    [Fact] // case 65 (cmd)
    public void Enable_Cmd_AssignmentsThenUnset()
    {
        using var tmp = new TempDir();
        using var env = new EnvScope();
        env.Set("RBMANAGER_VSDEVCMD", Bat(tmp, "set RB_TEST_NEW=hello"));
        using var cap = new ConsoleCapture();

        int rc = Msvc.Enable("cmd");

        Assert.Equal(0, rc);
        Assert.Contains("set \"RB_TEST_NEW=hello\"", cap.OutLines);
        Assert.Equal(CmdUnset, cap.OutLines[^1]);
    }

    [Fact] // case 66: no toolchain -> stderr warning, exit 1, stdout empty
    public void EnableAndExec_NoToolchain_WarnOnStderr()
    {
        using var tmp = new TempDir();
        using var env = new EnvScope();
        env.Set("RBMANAGER_VSDEVCMD", null); // force real discovery
        using var vsw = new VsWhereScope(tmp.At("no-vswhere.exe"));

        using (var cap = new ConsoleCapture())
        {
            int rc = Msvc.Enable("powershell");
            Assert.Equal(1, rc);
            Assert.Equal("", cap.Out);
            Assert.Contains("winget install Microsoft.VisualStudio.2022.BuildTools", cap.Err);
        }

        using (var cap = new ConsoleCapture())
        {
            int rc = Msvc.Exec(["cmd", "/c", "echo", "x"]);
            Assert.Equal(1, rc);
            Assert.Equal("", cap.Out);
            Assert.Contains("no Visual Studio C++ toolchain found", cap.Err);
        }
    }

    [Fact] // case 67
    public void LocateVsDevCmd_VsWhereMissing_Null()
    {
        using var tmp = new TempDir();
        using var env = new EnvScope();
        env.Set("RBMANAGER_VSDEVCMD", null);
        using var vsw = new VsWhereScope(tmp.At("no-vswhere.exe"));

        Assert.Null(Msvc.LocateVsDevCmd());
    }

    [Fact] // case 68: Exec applies the delta and drops NoDefault... in the child
    public void Exec_ChildSeesDelta_AndNoDefaultRemoved()
    {
        using var tmp = new TempDir();
        using var env = new EnvScope();
        env.Set("RBMANAGER_VSDEVCMD", Bat(tmp, "set RB_TEST_NEW=hello"));
        env.Set(NoDefault, "1");
        string dump = Bat(tmp, "set > \"%~1\"");
        string outFile = tmp.At("env.txt");

        int rc = Msvc.Exec([dump, outFile]);

        Assert.Equal(0, rc);
        string dumped = File.ReadAllText(outFile);
        Assert.Contains("RB_TEST_NEW=hello", dumped);
        Assert.DoesNotContain($"{NoDefault}=", dumped);
    }

    [Fact] // case 69
    public void Exec_PropagatesChildExitCode()
    {
        using var tmp = new TempDir();
        using var env = new EnvScope();
        env.Set("RBMANAGER_VSDEVCMD", Bat(tmp)); // no-op stub, exits 0

        int rc = Msvc.Exec(["cmd", "/c", "exit", "7"]);

        Assert.Equal(7, rc);
    }

    [Fact] // case 70: Exec resolves a .cmd shim off the activated PATH
    public void Exec_ResolvesCmdShimOnActivatedPath()
    {
        using var tmp = new TempDir();
        string shimDir = tmp.At("shim");
        Directory.CreateDirectory(shimDir);
        File.WriteAllText(Path.Combine(shimDir, "hello.cmd"), "@echo off\r\nexit /b 42\r\n");
        using var env = new EnvScope();
        env.Set("RBMANAGER_VSDEVCMD", Bat(tmp, $"set PATH={shimDir};%PATH%"));

        int rc = Msvc.Exec(["hello"]);

        Assert.Equal(42, rc);
    }

    [Fact] // case 71: an argument with spaces survives as one argument
    public void Exec_QuotesArgumentWithSpaces()
    {
        using var tmp = new TempDir();
        using var env = new EnvScope();
        env.Set("RBMANAGER_VSDEVCMD", Bat(tmp)); // no-op stub
        string echo = Bat(tmp, ">\"%~2\" echo %~1");
        string outFile = tmp.At("arg.txt");

        int rc = Msvc.Exec([echo, "a b c", outFile]);

        Assert.Equal(0, rc);
        Assert.Equal("a b c", File.ReadAllText(outFile).Trim());
    }
}
