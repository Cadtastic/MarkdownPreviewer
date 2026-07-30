namespace MarkdownPreviewer.Shell.Interop;

/// <summary>COM result codes used by this extension.</summary>
/// <remarks>
/// Every interface method returns one of these rather than throwing. An unhandled
/// managed exception crossing an in-process COM boundary takes down
/// <c>prevhost.exe</c>, and Explorer responds by disabling the preview pane — a
/// failure the user then has to hunt down in settings. Translating to an HRESULT
/// keeps a bad document a bad document.
/// </remarks>
internal static class HResult
{
    public const int Ok = 0;
    public const int False = 1;

    public const int Fail = unchecked((int)0x80004005);              // E_FAIL
    public const int NotImplemented = unchecked((int)0x80004001);    // E_NOTIMPL
    public const int InvalidArgument = unchecked((int)0x80070057);   // E_INVALIDARG
    public const int NoInterface = unchecked((int)0x80004002);       // E_NOINTERFACE
    public const int Pointer = unchecked((int)0x80004003);           // E_POINTER
    public const int Unexpected = unchecked((int)0x8000FFFF);        // E_UNEXPECTED
    public const int OutOfMemory = unchecked((int)0x8007000E);       // E_OUTOFMEMORY
}
