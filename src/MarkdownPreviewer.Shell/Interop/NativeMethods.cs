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

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetParent(IntPtr child, IntPtr newParent);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetFocus();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hwnd);
}
