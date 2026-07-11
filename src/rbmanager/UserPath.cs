using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RbManager;

internal static partial class UserPath
{
    private const nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x1A;
    private const uint SmtoAbortIfHung = 0x2;

    // Appends the entry to the per-user PATH if it is not already there,
    // preserving the existing value kind and unexpanded %VAR% references.
    // RBMANAGER_ENV_KEY redirects the write to an HKCU-relative scratch
    // subkey (and suppresses the broadcast) so tests never touch the real
    // PATH; production writes HKCU\Environment and broadcasts the change.
    public static void Ensure(string entry)
    {
        string? sub = Environment.GetEnvironmentVariable("RBMANAGER_ENV_KEY");
        if (sub is { Length: > 0 })
        {
            using var scratch = Registry.CurrentUser.CreateSubKey(sub, writable: true);
            Ensure(entry, scratch, broadcast: false);
            return;
        }
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
            ?? throw new IOException("cannot open HKCU\\Environment");
        Ensure(entry, key, broadcast: true);
    }

    internal static void Ensure(string entry, RegistryKey environmentKey, bool broadcast)
    {
        string path = environmentKey
            .GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string ?? "";
        bool present = path
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => string.Equals(p, entry, StringComparison.OrdinalIgnoreCase));
        if (present) return;

        RegistryValueKind kind;
        try { kind = environmentKey.GetValueKind("Path"); }
        catch (IOException) { kind = RegistryValueKind.ExpandString; }
        string updated = path.Length == 0 ? entry : $"{path.TrimEnd(';')};{entry}";
        environmentKey.SetValue("Path", updated, kind);

        if (broadcast)
            SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, "Environment",
                SmtoAbortIfHung, 5000, out _);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam,
        string lParam, uint flags, uint timeout, out nint result);
}
