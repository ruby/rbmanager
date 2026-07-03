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
    public static void Ensure(string entry)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
            ?? throw new IOException("cannot open HKCU\\Environment");
        string path = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string ?? "";
        bool present = path
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => string.Equals(p, entry, StringComparison.OrdinalIgnoreCase));
        if (present) return;

        RegistryValueKind kind;
        try { kind = key.GetValueKind("Path"); }
        catch (IOException) { kind = RegistryValueKind.ExpandString; }
        string updated = path.Length == 0 ? entry : $"{path.TrimEnd(';')};{entry}";
        key.SetValue("Path", updated, kind);

        SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, "Environment",
            SmtoAbortIfHung, 5000, out _);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam,
        string lParam, uint flags, uint timeout, out nint result);
}
