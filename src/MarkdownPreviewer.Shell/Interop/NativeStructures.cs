using System.Runtime.InteropServices;

namespace MarkdownPreviewer.Shell.Interop;

/// <summary>Win32 <c>RECT</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public System.Drawing.Rectangle ToRectangle() =>
        System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
}

/// <summary>Win32 <c>MSG</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int PointX;
    public int PointY;
}

/// <summary>Win32 <c>LOGFONTW</c>, as handed to <c>IPreviewHandlerVisuals.SetFont</c>.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public struct LOGFONT
{
    public int Height;
    public int Width;
    public int Escapement;
    public int Orientation;
    public int Weight;
    public byte Italic;
    public byte Underline;
    public byte StrikeOut;
    public byte CharSetValue;
    public byte OutPrecision;
    public byte ClipPrecision;
    public byte Quality;
    public byte PitchAndFamily;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string FaceName;
}

/// <summary>Win32 <c>PREVIEWHANDLERFRAMEINFO</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PREVIEWHANDLERFRAMEINFO
{
    public IntPtr AcceleratorTable;
    public uint AcceleratorEntryCount;
}
