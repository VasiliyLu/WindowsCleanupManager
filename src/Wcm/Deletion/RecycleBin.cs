using System.Runtime.InteropServices;

namespace Wcm.Deletion;

internal static partial class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    // Shows a prompt if something is too big for the bin instead of silently nuking it
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Returns null on success, otherwise an error description.</summary>
    public static string? Send(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return null;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // Double-null-terminated list
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING
        };
        var rc = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) return "операция прервана";
        return rc == 0 ? null : $"SHFileOperation 0x{rc:X}";
    }
}
