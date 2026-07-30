using System.Diagnostics;
using System.Text.Json;

namespace RbManager;

// PROTOTYPE: MSVC build-environment activation for the mswin packages.
//
// The official mswin binary ships no compiler, so `gem install <native>`
// has no toolchain on PATH and fails with mkmf's cryptic "install
// development tools first". This locates an installed Visual Studio (or
// Build Tools) MSVC toolchain, activates it the same way ruby/actions'
// mswin-build workflow does (VsDevCmd.bat, the script behind Visual
// Studio's Developer Command Prompt), and exposes that environment two
// ways:
//
//   rb msvc [--vsver <year>] [--] <command...>
//                                         run one command with the
//                                         toolchain already applied
//                                         (no shell mutation)
//   rb msvc enable [--vsver <year>] [cmd|powershell|pwsh]
//                                         print env assignments to eval
//                                         in the current shell
//   rb msvc --list                        list installed VS C++ toolchains
//
// The VS version is picked as --vsver flag > RBMANAGER_VSVER > newest
// installed. See docs/msvc-enable.md for the design rationale.
internal static class Msvc
{
    // Product year <-> installationVersion major. The year is the
    // user-facing name (displayName, installer branding, winget ids); the
    // major is what installationVersion carries. The year cannot be read
    // from catalog.productLineVersion: the Dev18 series reports "18" there
    // even though its displayName says 2026.
    private static readonly (string Year, int Major)[] VsProducts =
        [("2017", 15), ("2019", 16), ("2022", 17), ("2026", 18)];

    // Product year -> vswhere -version range.
    internal static readonly IReadOnlyDictionary<string, string> VsVerRanges =
        VsProducts.ToDictionary(p => p.Year, p => $"[{p.Major}.0,{p.Major + 1}.0)");

    private static string? YearOfVersion(string installationVersion) =>
        Version.TryParse(installationVersion, out Version? v) &&
        VsProducts.FirstOrDefault(p => p.Major == v.Major) is { Year: { } year }
            ? year : null;

    // vswhere ships at a fixed, versionless path with the VS Installer and
    // is the only supported way to locate installs (including Build-Tools-
    // only ones, which require -products *). Settable (and RBMANAGER_VSWHERE-
    // seeded) so tests can point it at a stub or a nonexistent path.
    internal static string VsWhere { get; set; } =
        Environment.GetEnvironmentVariable("RBMANAGER_VSWHERE") is { Length: > 0 } vsw
            ? vsw
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");

    // RBMANAGER_VSDEVCMD short-circuits VS discovery with a caller-supplied
    // VsDevCmd.bat (a stub in tests), so Enable/Exec are exercisable without
    // a real Visual Studio install.
    private static string? ResolveVsDevCmd(string? year) =>
        Environment.GetEnvironmentVariable("RBMANAGER_VSDEVCMD") is { Length: > 0 } stub
            ? stub
            : LocateVsDevCmd(year);

    // Resolves the effective VS product year: the --vsver flag wins over
    // RBMANAGER_VSVER; null (or the explicit "latest", useful to override
    // the env var per invocation) means the newest installed.
    internal static string? EffectiveVsVer(string? flag)
    {
        string? v = flag is { Length: > 0 }
            ? flag
            : Environment.GetEnvironmentVariable("RBMANAGER_VSVER");
        if (v is null or "" or "latest") return null;
        if (!VsVerRanges.ContainsKey(v))
            throw new InvalidOperationException(
                $"unknown Visual Studio version '{v}' " +
                $"(expected {string.Join(", ", VsProducts.Select(p => p.Year))}, or latest)");
        return v;
    }

    // What `rb msvc <args>` resolves to. `enable` is the only word
    // reserved after `msvc`; every other bare word is the user's command,
    // so a future operation has to be spelled as a flag (like --list)
    // rather than as a word that would shadow a real executable.
    internal enum Op { Run, Enable, List }

    private const string EnableWord = "enable";

    // Arguments after the `msvc` token: the leading options (--list,
    // --vsver <year>, --vsver=<year>) followed by either the `enable`
    // subcommand or the command to run. Options are recognized only
    // before the first non-option token, and `--` ends option reading,
    // so the user command is never reinterpreted. Returns null when the
    // arguments do not parse (caller prints usage).
    internal static (Op Op, string? Shell, string[] Command, string? VsVer)? Parse(string[] args)
    {
        bool list = false;
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a == "--list") { list = true; i++; }
            else if (a == "--vsver")
            {
                if (i + 1 >= args.Length) return null;
                i += 2;
            }
            else if (a.StartsWith("--vsver=", StringComparison.Ordinal)) i++;
            else break;
        }
        string[] rest = args[i..];

        // --list reports instead of acting, so it is terminal: nothing
        // may follow it, and a year (which only narrows what enable and
        // the passthrough activate) does not apply to it.
        if (list) return rest.Length == 0 ? (Op.List, null, [], null) : null;

        // The `enable` token is dropped and everything else handed to
        // EnableArgs, so --vsver parses on either side of it.
        if (rest is [EnableWord, ..])
            return EnableArgs([.. args[..i], .. rest[1..]]) is { } en
                ? (Op.Enable, en.Shell, [], en.VsVer)
                : null;

        return ExecArgs(args) is { } ex ? (Op.Run, null, ex.Command, ex.VsVer) : null;
    }

    public static int Dispatch((Op Op, string? Shell, string[] Command, string? VsVer) request) =>
        request.Op switch
        {
            Op.List => List(),
            Op.Enable => Enable(request.Shell, request.VsVer),
            _ => Exec(request.Command, request.VsVer),
        };

    // enable arguments: [--vsver <year>] [shell], in either order.
    // Returns null when the arguments do not parse (caller prints usage).
    internal static (string? Shell, string? VsVer)? EnableArgs(string[] args)
    {
        string? shell = null, vsver = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--vsver")
            {
                if (++i >= args.Length) return null;
                vsver = args[i];
            }
            else if (a.StartsWith("--vsver=", StringComparison.Ordinal))
            {
                vsver = a["--vsver=".Length..];
                if (vsver.Length == 0) return null;
            }
            else if (a.StartsWith('-') || shell is not null) return null;
            else shell = a;
        }
        return (shell, vsver);
    }

    // passthrough arguments: [--vsver <year>] [--] <command...>. Options
    // are recognized only before the command, so the user command is never
    // reinterpreted; `--` ends option parsing for commands that start
    // with a dash. Returns null when the arguments do not parse or no
    // command remains (caller prints usage).
    internal static (string[] Command, string? VsVer)? ExecArgs(string[] args)
    {
        string? vsver = null;
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a == "--") { i++; break; }
            if (a == "--vsver")
            {
                if (i + 1 >= args.Length) return null;
                vsver = args[i + 1];
                i += 2;
            }
            else if (a.StartsWith("--vsver=", StringComparison.Ordinal))
            {
                vsver = a["--vsver=".Length..];
                if (vsver.Length == 0) return null;
                i++;
            }
            else if (a.StartsWith("--", StringComparison.Ordinal)) return null;
            else break;
        }
        return i < args.Length ? (args[i..], vsver) : null;
    }

    public static int Enable(string? shell, string? vsver = null)
    {
        Shell target = ParseShell(shell);
        string? year = EffectiveVsVer(vsver);
        string? vsdevcmd = ResolveVsDevCmd(year);
        if (vsdevcmd is null) return WarnMissingToolchain(year);
        var env = ActivatedDelta(vsdevcmd).ToList();
        if (!WindowsSdkLibsPresent(ByKey(env))) return WarnMissingWindowsSdk();
        if (LibclangPathToSet(vsdevcmd) is { } libclangBin)
            env.Add(("LIBCLANG_PATH", libclangBin));
        foreach ((string key, string value) in env)
            Console.WriteLine(Assignment(target, key, value));
        // The mkmf gotcha: with NoDefaultCurrentDirectoryInExePath set,
        // try_link runs the freshly built conftest by bare name and fails.
        // Clear it for the activated shell.
        Console.WriteLine(Unset(target, "NoDefaultCurrentDirectoryInExePath"));
        return 0;
    }

    public static int Exec(string[] command, string? vsver = null)
    {
        string? year = EffectiveVsVer(vsver);
        string? vsdevcmd = ResolveVsDevCmd(year);
        if (vsdevcmd is null) return WarnMissingToolchain(year);
        var delta = ActivatedDelta(vsdevcmd).ToList();
        if (!WindowsSdkLibsPresent(ByKey(delta))) return WarnMissingWindowsSdk();
        if (LibclangPathToSet(vsdevcmd) is { } libclangBin)
            delta.Add(("LIBCLANG_PATH", libclangBin));
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

    private static Dictionary<string, string> ByKey(List<(string, string)> pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase);

    // bindgen (via clang-sys, an rb-sys/rustc-bindgen dependency) probes the
    // VS install for libclang.dll itself; it does not consult PATH. VS
    // ships both an ARM64 and an x64 copy of libclang.dll side by side, and
    // clang-sys's search picks between them non-deterministically. Grabbing
    // the ARM64 one fails every build on an x64 host with a LoadLibraryExW
    // error. VsDevCmd itself never sets LIBCLANG_PATH, so this is a
    // separate step layered on top of ActivatedDelta. A LIBCLANG_PATH the
    // user already has set is left untouched.
    // TODO(arm64): "x64" becomes host-arch-dependent here, same as the
    // -arch=amd64/-host_arch=amd64 in ActivatedDelta.
    internal static string? LibclangPathToSet(string vsdevcmd)
    {
        if (Environment.GetEnvironmentVariable("LIBCLANG_PATH") is { Length: > 0 })
            return null;
        // vsdevcmd is <installationPath>\Common7\Tools\VsDevCmd.bat.
        string? installationPath =
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(vsdevcmd)));
        if (installationPath is null) return null;
        string bin = Path.Combine(installationPath, "VC", "Tools", "Llvm", "x64", "bin");
        return File.Exists(Path.Combine(bin, "libclang.dll")) ? bin : null;
    }

    // vswhere's -requires only confirms the VC.Tools.x86.x64 component is
    // installed, not that its paired Windows SDK's library files are
    // actually on disk; that gap surfaces much later as a bewildering
    // linker error (or, worse, an unrelated-looking one if a non-MSVC
    // link.exe shadows the real one on PATH). There is no stable vswhere
    // component id for "this SDK version's libs exist", so this checks the
    // lib tree directly, keyed off what VsDevCmd itself just reported in
    // the activation delta. WindowsSDKLibVersion is the Lib subdirectory
    // name in both the 8.1 (winv6.3) and 10 (10.0.x) layouts;
    // WindowsSDKVersion, which only the 10 SDK sets, is the fallback.
    // Neither appearing means VsDevCmd reported no SDK change and there is
    // nothing to check against, so the check is skipped rather than
    // failing closed.
    // TODO(arm64): the x64 subdirectory becomes host-arch-dependent here.
    internal static bool WindowsSdkLibsPresent(IReadOnlyDictionary<string, string> activated)
    {
        if (!activated.TryGetValue("WindowsSdkDir", out string? sdkDir)) return true;
        if (!activated.TryGetValue("WindowsSDKLibVersion", out string? libVer) &&
            !activated.TryGetValue("WindowsSDKVersion", out libVer))
            return true;
        return File.Exists(Path.Combine(
            sdkDir, "Lib", libVer.TrimEnd('\\', '/'), "um", "x64", "kernel32.lib"));
    }

    // Runs VsDevCmd in a clean child and returns only the variables it added
    // or changed relative to our own (i.e. the calling shell's) environment.
    internal static IEnumerable<(string, string)> ActivatedDelta(string vsdevcmd)
    {
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

    // Resolves the newest VS install that carries the MSVC toolset (within
    // the given product year when one is requested) and returns its
    // VsDevCmd.bat, or null when no usable toolchain exists (vswhere
    // absent, no matching install, or VsDevCmd.bat missing).
    internal static string? LocateVsDevCmd(string? year = null)
    {
        if (!File.Exists(VsWhere)) return null;
        var psi = new ProcessStartInfo
        {
            FileName = VsWhere,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // -products * finds Build-Tools-only installs; -requires narrows to
        // installs that carry the MSVC x64/x86 toolset (this component id is
        // stable across VS versions, unlike the legacy Microsoft.VisualCpp.*
        // ids); -version narrows to the requested product year; -latest
        // picks the newest when several qualify.
        foreach (string a in new[]
        {
            "-latest", "-products", "*",
            "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            "-property", "installationPath",
        }) psi.ArgumentList.Add(a);
        if (year is not null)
        {
            psi.ArgumentList.Add("-version");
            psi.ArgumentList.Add(VsVerRanges[year]);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to launch vswhere");
        string path = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        if (path.Length == 0) return null;
        string vsdevcmd = Path.Combine(path, "Common7", "Tools", "VsDevCmd.bat");
        return File.Exists(vsdevcmd) ? vsdevcmd : null;
    }

    internal sealed record VsInstall(string Year, string Version, string Path);

    // All installs carrying the MSVC toolset, newest first (the first entry
    // is what -latest resolves to). Empty when vswhere is absent or finds
    // nothing. The year comes from the installationVersion major (see
    // VsProducts), falling back to catalog.productLineVersion for majors
    // this build does not know yet.
    internal static List<VsInstall> Installs()
    {
        if (!File.Exists(VsWhere)) return [];
        var psi = new ProcessStartInfo
        {
            FileName = VsWhere,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string a in new[]
        {
            "-products", "*",
            "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            "-format", "json", "-utf8",
        }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to launch vswhere");
        string json = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0) return [];

        var installs = new List<VsInstall>();
        using var doc = JsonDocument.Parse(json);
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            if (e.GetProperty("installationPath").GetString() is not { } path ||
                e.GetProperty("installationVersion").GetString() is not { } version)
                continue;
            string year = YearOfVersion(version)
                ?? (e.TryGetProperty("catalog", out JsonElement catalog) &&
                    catalog.TryGetProperty("productLineVersion", out JsonElement line) &&
                    line.GetString() is { Length: > 0 } y
                        ? y : "?");
            installs.Add(new VsInstall(year, version, path));
        }
        return installs
            .OrderByDescending(i => System.Version.TryParse(i.Version, out var v)
                ? v : new Version(0, 0))
            .ToList();
    }

    // `rb msvc --list`: one line per install, `*` marking what the current
    // default resolution (--vsver unset, so RBMANAGER_VSVER or newest)
    // would pick, in the same style as `rb list`.
    public static int List()
    {
        var installs = Installs();
        if (installs.Count == 0) return WarnMissingToolchain(null);
        string? year = EffectiveVsVer(null);
        VsInstall? picked = year is null
            ? installs[0]
            : installs.FirstOrDefault(i => i.Year == year);
        int width = installs.Max(i => i.Version.Length);
        foreach (VsInstall i in installs)
            Console.WriteLine(
                $"{(ReferenceEquals(i, picked) ? "*" : " ")} {i.Year}  " +
                $"{i.Version.PadRight(width)}  {i.Path}");
        return 0;
    }

    // Fails fast with the setup steps instead of letting mkmf die later
    // with its cryptic "install development tools first". stderr only, so
    // an eval'd `rb msvc enable` pipeline never swallows it. When a
    // specific year was requested, names it, lists the years that are
    // installed, and suggests the matching Build Tools package.
    private static int WarnMissingToolchain(string? year)
    {
        string product = year is null ? "" : $" {year}";
        string installed = "";
        if (year is not null &&
            Installs().Select(i => i.Year).Distinct().Order().ToArray() is { Length: > 0 } years)
            installed = $" (installed: {string.Join(", ", years)})";
        Console.Error.WriteLine($"""
            rb: warning: no Visual Studio{product} C++ toolchain found{installed}; native extensions cannot be built.

            To set one up:

              1. Install the "Desktop development with C++" workload, e.g.

                   winget install Microsoft.VisualStudio.{year ?? "2022"}.BuildTools --override "--quiet --add Microsoft.VisualStudio.Workload.VCTools"

                 (any Visual Studio edition with that workload also works)

              2. Open a new terminal and re-run this command.
            """);
        return 1;
    }

    // The compiler is present but has nothing to link against. Refusing here
    // beats activating a half-usable environment: the failure would
    // otherwise land in the linker, where a missing kernel32.lib reads as a
    // gem bug rather than an incomplete VS install.
    private static int WarnMissingWindowsSdk()
    {
        Console.Error.WriteLine("""
            rb: warning: the Visual Studio install has the MSVC compiler but no Windows SDK libraries; linking will fail.

            To set one up:

              1. Open the Visual Studio Installer, Modify the install, and add the
                 "Windows 11 SDK" (or "Windows 10 SDK") component under
                 "Desktop development with C++".

              2. Open a new terminal and re-run this command.
            """);
        return 1;
    }

    internal enum Shell { Cmd, PowerShell }

    internal static Shell ParseShell(string? shell) => shell switch
    {
        null or "powershell" or "pwsh" or "ps" => Shell.PowerShell,
        "cmd" or "bat" => Shell.Cmd,
        _ => throw new InvalidOperationException($"unknown shell '{shell}'"),
    };

    internal static string Assignment(Shell shell, string key, string value) => shell switch
    {
        Shell.Cmd => $"set \"{key}={value}\"",
        // Single-quoted PowerShell literal; ' is escaped by doubling.
        _ => $"$env:{key} = '{value.Replace("'", "''")}'",
    };

    internal static string Unset(Shell shell, string key) => shell switch
    {
        Shell.Cmd => $"set \"{key}=\"",
        _ => $"Remove-Item Env:\\{key} -ErrorAction SilentlyContinue",
    };

    // Minimal Windows argument quoting for the cmd /c command line.
    internal static string QuoteArg(string arg) =>
        arg.Length > 0 && !arg.Any(char.IsWhiteSpace) ? arg : $"\"{arg}\"";
}
