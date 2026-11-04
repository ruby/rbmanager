using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace RbManager;

// The official mswin package does not bundle vcruntime140.dll: app-local
// deployment of the VC runtime is deprecated by Microsoft (it never receives
// Windows Update fixes), so ruby.exe expects the machine-wide VC++
// Redistributable and dies with STATUS_DLL_NOT_FOUND on a machine without
// it. `rb setup` checks for it and offers to install; install/use warn but
// do not block.
//
// https://bugs.ruby-lang.org/issues/22180
internal static partial class VcRedist
{
    private const string RuntimesKey = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes";
    // Microsoft's permalink to the latest 2015-2022 x64 redistributable.
    // On ARM64 Windows it installs the ARM64 runtime alongside the x64 one.
    private const string InstallerUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    // The Runtimes subkey ("x64" or "ARM64") a ruby package or platform needs.
    internal static string RuntimeOf(string rubyOrPlatform) =>
        rubyOrPlatform.EndsWith(BinaryIndex.Arm64, StringComparison.OrdinalIgnoreCase)
            ? "ARM64"
            : "x64";

    // Registry probe, the documented way to detect the 2015-2022 runtime.
    // RBMANAGER_VC_REDIST_KEY redirects the Runtimes key to an HKCU-relative
    // scratch subkey so tests never depend on the host machine's real state.
    internal static string? InstalledVersion(string runtime)
    {
        if (Environment.GetEnvironmentVariable("RBMANAGER_VC_REDIST_KEY")
            is { Length: > 0 } sub)
        {
            using var scratch = Registry.CurrentUser.OpenSubKey($@"{sub}\{runtime}");
            return VersionOf(scratch);
        }
        // The redist setup is a 32-bit process; probe both views so a key
        // registered under WOW6432Node only is still found.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey($@"{RuntimesKey}\{runtime}");
            if (VersionOf(key) is { } version) return version;
        }
        return null;
    }

    private static string? VersionOf(RegistryKey? key) =>
        key?.GetValue("Installed") is 1
            ? key.GetValue("Version") as string ?? "(unknown version)"
            : null;

    // Fallback when the registry key is absent: the runtime DLL itself in
    // the system directory. RBMANAGER_VCRUNTIME overrides the path for tests.
    internal static bool RuntimeDllExists()
    {
        string dll = Environment.GetEnvironmentVariable("RBMANAGER_VCRUNTIME")
            is { Length: > 0 } path
            ? path
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "vcruntime140.dll");
        return File.Exists(dll);
    }

    // On ARM64 the x64 and ARM64 runtimes share the one vcruntime140.dll
    // path in the system directory, so finding it there does not say which
    // of them is installed. Only the Runtimes keys can tell.
    private static bool DllMayStandIn => BinaryIndex.Platforms is [_];

    internal static bool IsInstalled(string runtime) =>
        InstalledVersion(runtime) is not null || (DllMayStandIn && RuntimeDllExists());

    public static async Task<int> Ensure(bool assumeYes)
    {
        // There is no ruby to ask yet, so cover every platform rb picks on
        // this machine. On ARM64 that includes x64 for the fallback builds.
        string[] runtimes = BinaryIndex.Platforms.Select(RuntimeOf).ToArray();
        string?[] versions = runtimes.Select(InstalledVersion).ToArray();
        if (versions.All(v => v is not null))
        {
            Console.WriteLine($"VC++ Redistributable {versions[^1]} is already installed.");
            return 0;
        }
        if (DllMayStandIn && RuntimeDllExists())
        {
            Console.WriteLine("vcruntime140.dll is already present in the system directory.");
            return 0;
        }

        string missing = string.Join(", ", runtimes.Where((_, i) => versions[i] is null));
        Console.WriteLine($"""
            The Microsoft Visual C++ Redistributable ({missing}) is not installed.
            Ruby's official Windows packages rely on it; without vcruntime140.dll
            ruby.exe cannot start. Installing it affects the whole machine and
            prompts for administrator approval (UAC). Without administrator
            rights, ask an administrator to install {InstallerUrl} instead.
            """);
        if (!assumeYes)
        {
            Console.Write("Download and install it now? [y/N] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                Console.Error.WriteLine(
                    "rb: VC++ Redistributable installation declined; run `rb setup` again to install it");
                return 1;
            }
        }
        return await Install(runtimes);
    }

    // Non-blocking companion for install/use: the selected ruby stays in
    // place, it just cannot start until the runtime shows up.
    public static void WarnIfMissing(string ruby)
    {
        string runtime = RuntimeOf(ruby);
        if (IsInstalled(runtime)) return;
        Console.Error.WriteLine($"""
            rb: warning: the Microsoft Visual C++ Redistributable ({runtime}) was not found.
            ruby.exe cannot start without vcruntime140.dll. Run `rb setup` to install it.
            """);
    }

    private static async Task<int> Install(string[] runtimes)
    {
        string installer = Path.Combine(Path.GetTempPath(), "vc_redist.x64.exe");
        Console.WriteLine($"Downloading {InstallerUrl} ...");
        using (var http = new HttpClient())
        await using (var body = await http.GetStreamAsync(InstallerUrl))
        await using (var file = File.Create(installer))
        {
            await body.CopyToAsync(file);
        }

        try
        {
            VerifyMicrosoftSignature(installer);

            Console.WriteLine("Installing (a UAC prompt will appear) ...");
            int code;
            try
            {
                code = RunElevated(installer, "/install /quiet /norestart");
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED
            {
                Console.Error.WriteLine(
                    "rb: elevation declined; the VC++ Redistributable was not installed");
                return 1;
            }

            switch (code)
            {
                case 0:
                case 1638: // a newer version is already installed
                    break;
                case 3010: // ERROR_SUCCESS_REBOOT_REQUIRED
                    Console.WriteLine("Windows wants a reboot to finish the installation.");
                    break;
                default:
                    Console.Error.WriteLine(
                        $"rb: vc_redist.x64.exe failed with exit code {code}");
                    return 1;
            }
            if (!DllMayStandIn &&
                runtimes.Where(r => InstalledVersion(r) is null).ToArray() is { Length: > 0 } missing)
            {
                Console.Error.WriteLine(
                    $"rb: the VC++ Redistributable ({string.Join(", ", missing)}) " +
                    "is still not registered after vc_redist.x64.exe");
                return 1;
            }
            Console.WriteLine(InstalledVersion("x64") is { } installed
                ? $"Installed VC++ Redistributable {installed}."
                : "Installed the VC++ Redistributable.");
            return 0;
        }
        finally
        {
            File.Delete(installer);
        }
    }

    // ShellExecute + runas: the redistributable installs per-machine, the
    // one place rbmanager's no-elevation design cannot hold.
    private static int RunElevated(string file, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the installer");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    // The download rides on TLS to aka.ms, but never run a downloaded
    // binary elevated unless it carries a valid Authenticode signature
    // whose subject is Microsoft.
    internal static void VerifyMicrosoftSignature(string file)
    {
        if (WinVerifyTrustFile(file) != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(file)} does not have a valid Authenticode signature");
        using X509Certificate signer = X509Certificate.CreateFromSignedFile(file);
        if (!signer.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{Path.GetFileName(file)} is not signed by Microsoft: {signer.Subject}");
    }

    private static unsafe int WinVerifyTrustFile(string file)
    {
        // WINTRUST_ACTION_GENERIC_VERIFY_V2
        Guid action = new(0x00aac56b, 0xcd44, 0x11d0,
            0x8c, 0xc2, 0x00, 0xc0, 0x4f, 0xc2, 0x95, 0xee);
        fixed (char* path = file)
        {
            var info = new WinTrustFileInfo
            {
                Size = (uint)sizeof(WinTrustFileInfo),
                FilePath = path,
            };
            var data = new WinTrustData
            {
                Size = (uint)sizeof(WinTrustData),
                UiChoice = 2,         // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE: no network dependency
                UnionChoice = 1,      // WTD_CHOICE_FILE
                File = &info,
                StateAction = 1,      // WTD_STATEACTION_VERIFY
            };
            int status = WinVerifyTrust(0, &action, &data);
            data.StateAction = 2;     // WTD_STATEACTION_CLOSE
            WinVerifyTrust(0, &action, &data);
            return status;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WinTrustFileInfo
    {
        public uint Size;
        public char* FilePath;
        public nint FileHandle;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public WinTrustFileInfo* File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static unsafe partial int WinVerifyTrust(nint hwnd, Guid* actionId, WinTrustData* data);
}
