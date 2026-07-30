using System.Runtime.InteropServices;

namespace MarkdownPreviewer.Rendering.Interop;

/// <summary>
/// The Win32 activation contract for <c>DataTransferManager</c>.
/// </summary>
/// <remarks>
/// <c>DataTransferManager.GetForCurrentView()</c> only works inside a UWP view.
/// Desktop processes get theirs per-HWND through this documented shell interop
/// interface (shobjidl_core.h, IID from the Windows SDK).
/// </remarks>
[ComImport]
[Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataTransferManagerInterop
{
    /// <returns>The ABI pointer of the window's <c>DataTransferManager</c>.</returns>
    nint GetForWindow([In] nint appWindow, [In] ref Guid riid);

    void ShowShareUIForWindow([In] nint appWindow);
}
