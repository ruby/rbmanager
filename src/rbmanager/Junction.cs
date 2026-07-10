using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RbManager;

// NTFS directory junction (mount point). Unlike symbolic links, junctions
// can be created without SeCreateSymbolicLinkPrivilege or Developer Mode,
// which is why `current` is a junction and not a symlink.
internal static partial class Junction
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x7; // read | write | delete
    private const uint OpenExisting = 3;
    private const uint BackupSemanticsOpenReparse = 0x02000000 | 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint MountPointTag = 0xA0000003;

    public static void Create(string junction, string target)
    {
        string full = Path.GetFullPath(target).TrimEnd('\\');
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"junction target does not exist: {full}");
        Directory.CreateDirectory(junction);

        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + full);
        byte[] print = Encoding.Unicode.GetBytes(full);

        // REPARSE_DATA_BUFFER header + MountPointReparseBuffer
        int dataLength = 8 + substitute.Length + 2 + print.Length + 2;
        using var stream = new MemoryStream(8 + dataLength);
        using var writer = new BinaryWriter(stream);
        writer.Write(MountPointTag);
        writer.Write((ushort)dataLength);
        writer.Write((ushort)0);                       // Reserved
        writer.Write((ushort)0);                       // SubstituteNameOffset
        writer.Write((ushort)substitute.Length);
        writer.Write((ushort)(substitute.Length + 2)); // PrintNameOffset
        writer.Write((ushort)print.Length);
        writer.Write(substitute);
        writer.Write((ushort)0);                       // terminator
        writer.Write(print);
        writer.Write((ushort)0);                       // terminator
        byte[] buffer = stream.ToArray();

        using SafeFileHandle handle = CreateFileW(junction, GenericWrite, ShareAll,
            IntPtr.Zero, OpenExisting, BackupSemanticsOpenReparse, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"cannot open {junction} (error {Marshal.GetLastPInvokeError()})");
        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length,
                IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException($"cannot create junction (error {Marshal.GetLastPInvokeError()})");
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        byte[] inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}
