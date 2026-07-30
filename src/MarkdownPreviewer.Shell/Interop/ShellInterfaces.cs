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
/// <remarks>
/// Deliberately NOT implemented by <c>MarkdownPreviewHandler</c>. The shell
/// prefers it over every other initialisation interface, and a stream carries no
/// directory (its <c>Stat</c> name is a bare file name), which would make
/// relative images unresolvable for ordinary on-disk files. Omitting it makes
/// the shell fall through to <see cref="IInitializeWithItem"/>, which has the
/// real path — and still covers stream-only items via the item's own stream.
/// </remarks>
[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    [PreserveSig] int Initialize(IStream stream, uint mode);
}

/// <summary>
/// <c>IInitializeWithItem</c> — initialisation from a shell item.
/// </summary>
[ComImport]
[Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithItem
{
    [PreserveSig] int Initialize(IShellItem item, uint mode);
}

/// <summary>
/// <c>IShellItem</c> — the shell's handle to a namespace object.
/// </summary>
/// <remarks>Public because it appears in the signature of a public COM method on
/// <c>MarkdownPreviewHandler</c>; the structs in NativeStructures.cs are public
/// for the same reason.</remarks>
[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid bhid, ref Guid riid, out IntPtr ppv);

    [PreserveSig] int GetParent(out IShellItem parent);

    /// <param name="sigdnName">A SIGDN value; the string is CoTaskMem-allocated.</param>
    [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr name);

    [PreserveSig] int GetAttributes(uint mask, out uint attributes);

    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
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
