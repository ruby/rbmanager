using System.Diagnostics;

namespace RbManager;

// PROTOTYPE: a `ridk enable` equivalent for the mswin packages.
//
// The official mswin binary carries no devkit, so `gem install <native>`
// has no compiler on PATH and fails with mkmf's cryptic "install
// development tools first". This locates an installed Visual Studio (or
// Build Tools) C++ toolchain, activates it the same way ruby/actions'
// mswin-build workflow does (VsDevCmd.bat), and exposes that environment
// two ways:
//
//   rb enable [cmd|powershell|pwsh]  print env assignments to eval in the
//                                    current shell (ridk-parity)
//   rb exec -- <command...>          run one command with the toolchain
//                                    already applied (no shell mutation)
//
// See docs/devkit-enable.md for the design rationale.
internal static class Devkit
{
    // vswhere ships at a fixed, versionless path with the VS Installer and
    // is the only supported way to locate installs (including Build-Tools-
    // only ones, which require -products *).
    private static readonly string VsWhere = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft Visual Studio", "Installer", "vswhere.exe");

    private const string MissingVs =
        "no Visual Studio C++ toolchain found.\n" +
        "Install the \"Desktop development with C++\" workload, e.g.\n" +
        "  winget install Microsoft.VisualStudio.2022.BuildTools " +
        "--override \"--quiet --add Microsoft.VisualStudio.Workload.VCTools\"";

    public static int Enable(string? shell)
    {
        var env = ActivatedDelta();
        Shell target = ParseShell(shell);
        foreach ((string key, string value) in env)
            Console.WriteLine(Assignment(target, key, value));
        // The mkmf gotcha: with NoDefaultCurrentDirectoryInExePath set,
        // try_link runs the freshly built conftest by bare name and fails.
        // Clear it for the activated shell.
        Console.WriteLine(Unset(target, "NoDefaultCurrentDirectoryInExePath"));
        return 0;
    }

    public static int Exec(string[] command)
    {
        var delta = ActivatedDelta();
        // cmd.exe /c so that .cmd shims (gem, bundle) and PATHEXT resolve
        // the way they would if the user had typed the command directly.
        string commandLine = string.Join(' ', command.Select(QuoteArg));
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            // Raw Arguments (not ArgumentList): with /s, cmd strips the outer
            // quotes and runs the rest verbatim, so we control the quoting.
            Arguments = $"/s /c \"{commandLine}\"",
            UseShellExecute = false,
        };
        foreach ((string key, string value) in delta)
            psi.Environment[key] = value;
        psi.Environment.Remove("NoDefaultCurrentDirectoryInExePath");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start command");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    // Runs VsDevCmd in a clean child and returns only the variables it added
    // or changed relative to our own (i.e. the calling shell's) environment.
    private static IEnumerable<(string, string)> ActivatedDelta()
    {
        string install = LocateVs();
        string vsdevcmd = Path.Combine(install, "Common7", "Tools", "VsDevCmd.bat");
        string cmd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        // -no_logo suppresses the banner so stdout is just `set` output.
        // Raw Arguments so /s strips only the outer quotes, leaving the
        // quoted VsDevCmd path intact.
        // TODO(arm64): -arch/-host_arch would become arm64 here.
        var psi = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments =
                $"/s /c \"call \"{vsdevcmd}\" -arch=amd64 -host_arch=amd64 -no_logo && set\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to launch VsDevCmd");
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("VsDevCmd activation failed");

        var baseline = Environment.GetEnvironmentVariables();
        foreach (string line in output.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq];
            string value = line[(eq + 1)..].TrimEnd('\r');
            string? had = baseline[key] as string;
            if (!string.Equals(had, value, StringComparison.Ordinal))
                yield return (key, value);
        }
    }

    private static string LocateVs()
    {
        if (!File.Exists(VsWhere))
            throw new InvalidOperationException(MissingVs);
        var psi = new ProcessStartInfo
        {
            FileName = VsWhere,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // -products * finds Build-Tools-only installs; -requires narrows to
        // installs that carry the MSVC x64/x86 toolset (this component id is
        // stable across VS versions, unlike the legacy Microsoft.VisualCpp.*
        // ids); -latest picks the newest when several qualify.
        foreach (string a in new[]
        {
            "-latest", "-products", "*",
            "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            "-property", "installationPath",
        }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to launch vswhere");
        string path = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        if (path.Length == 0)
            throw new InvalidOperationException(MissingVs);
        return path;
    }

    private enum Shell { Cmd, PowerShell }

    private static Shell ParseShell(string? shell) => shell switch
    {
        null or "powershell" or "pwsh" or "ps" => Shell.PowerShell,
        "cmd" or "bat" => Shell.Cmd,
        _ => throw new InvalidOperationException($"unknown shell '{shell}'"),
    };

    private static string Assignment(Shell shell, string key, string value) => shell switch
    {
        Shell.Cmd => $"set \"{key}={value}\"",
        // Single-quoted PowerShell literal; ' is escaped by doubling.
        _ => $"$env:{key} = '{value.Replace("'", "''")}'",
    };

    private static string Unset(Shell shell, string key) => shell switch
    {
        Shell.Cmd => $"set \"{key}=\"",
        _ => $"Remove-Item Env:\\{key} -ErrorAction SilentlyContinue",
    };

    // Minimal Windows argument quoting for the cmd /c command line.
    private static string QuoteArg(string arg) =>
        arg.Length > 0 && !arg.Any(char.IsWhiteSpace) ? arg : $"\"{arg}\"";
}
