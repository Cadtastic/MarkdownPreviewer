using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MarkdownPreviewer.Shell.Interop;

/*
 * Interface IDs below were taken from the Windows SDK IDL (propsys.idl,
 * shobjidl_core.idl) and cross-checked against ReactOS's propsys.idl and the
 * Vanara P/Invoke bindings. A single wrong digit here presents as "the shell
 * silently never calls my handler", which is close to undiagnosable, so they are
 * recorded with their source rather than left as bare literals.
 *
 * Note in particular that IPreviewHandlerFrame is fec87aaf-35f9-447a-adb7-
 * 20234491401a. Several third-party blog posts quote a different value.
 */

/// <summary>
/// <c>IPreviewHandler</c> — the contract Explorer's preview pane calls.
/// </summary>
[ComImport]
[Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    [PreserveSig] int SetWindow(IntPtr hwnd, ref RECT rect);

    [PreserveSig] int SetRect(ref RECT rect);

    [PreserveSig] int DoPreview();

    [PreserveSig] int Unload();

    [PreserveSig] int SetFocus();

    [PreserveSig] int QueryFocus(out IntPtr focusedWindow);

    [PreserveSig] int TranslateAccelerator(ref MSG message);
}

/// <summary>
/// <c>IPreviewHandlerVisuals</c> — how the host tells us its colours and font.
/// </summary>
[ComImport]
[Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandlerVisuals
{
    [PreserveSig] int SetBackgroundColor(uint color);

    [PreserveSig] int SetFont(ref LOGFONT logFont);

    [PreserveSig] int SetTextColor(uint color);
}

/// <summary>
/// <c>IPreviewHandlerFrame</c> — implemented by the host, obtained from the site.
/// </summary>
[ComImport]
[Guid("fec87aaf-35f9-447a-adb7-20234491401a")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandlerFrame
{
    [PreserveSig] int GetWindowContext(out PREVIEWHANDLERFRAMEINFO frameInfo);

    [PreserveSig] int TranslateAccelerator(ref MSG message);
}

/// <summary>
/// <c>IInitializeWithFile</c> — initialisation from a file path.
/// </summary>
[ComImport]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint mode);
}

/// <summary>
/// <c>IInitializeWithStream</c> — initialisation from a stream.
/// </summary>
[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    [PreserveSig] int Initialize(IStream stream, uint mode);
}

/// <summary>
/// <c>IObjectWithSite</c> — how the host hands us its frame.
/// </summary>
[ComImport]
[Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectWithSite
{
    [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);

    [PreserveSig] int GetSite(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? site);
}

/// <summary>
/// <c>IOleWindow</c> — lets the host find our HWND.
/// </summary>
[ComImport]
[Guid("00000114-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleWindow
{
    [PreserveSig] int GetWindow(out IntPtr hwnd);

    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
}
