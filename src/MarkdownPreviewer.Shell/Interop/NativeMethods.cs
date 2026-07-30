using System.Runtime.InteropServices;

namespace MarkdownPreviewer.Shell.Interop;

/// <summary>Win32 entry points needed to live inside a host-supplied HWND.</summary>
internal static partial class NativeMethods
{
    public const int WS_CHILD = unchecked((int)0x40000000);
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_CONTROLPARENT = 0x00010000;

    /// <summary>Storage mode for read-only shell initialisation (<c>STGM_READ</c>).</summary>
    public const uint STGM_READ = 0x00000000;

    /// <summary><c>SIGDN_FILESYSPATH</c> — the item's file-system path, when it has one.</summary>
    public const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary><c>BHID_Stream</c> — bind an <c>IShellItem</c> to its content stream.</summary>
    public static readonly Guid BhidStream = new("1CEBB3AB-7C10-499a-A417-92CA16C4CB83");

    /// <summary>IID of <c>IStream</c>.</summary>
    public static readonly Guid IidIStream = new("0000000c-0000-0000-C000-000000000046");

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetParent(IntPtr child, IntPtr newParent);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetFocus();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
