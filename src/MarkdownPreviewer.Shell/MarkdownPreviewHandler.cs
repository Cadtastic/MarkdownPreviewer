using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Application.Preview;
using MarkdownPreviewer.Infrastructure.Configuration;
using MarkdownPreviewer.Infrastructure.Documents;
using MarkdownPreviewer.Shell.Composition;
using MarkdownPreviewer.Shell.Hosting;
using MarkdownPreviewer.Shell.Interop;

namespace MarkdownPreviewer.Shell;

/// <summary>
/// The COM class Explorer instantiates to preview a Markdown file.
/// </summary>
/// <remarks>
/// <para>This type is the boundary between the Windows Shell and everything else.
/// Its entire job is to translate COM calls into calls on
/// <see cref="PreviewSession"/> and to translate every possible failure into an
/// HRESULT. No business logic lives here.</para>
///
/// <para><b>Nothing may throw.</b> Every interface method is wrapped. An
/// exception escaping into <c>prevhost.exe</c> terminates the surrogate, and
/// Explorer's response to a surrogate that keeps dying is to stop showing
/// previews at all.</para>
///
/// <para><b>Nothing here runs on a UI thread.</b> The registry says
/// <c>ThreadingModel = Apartment</c>, but managed CCWs are apartment-agile, so the
/// shell's calls actually arrive on arbitrary RPC worker threads — no STA, no
/// message pump. Everything that touches a window or WebView2 is therefore
/// marshalled onto the handler's own <see cref="PreviewUiThread"/>.</para>
///
/// <para><b>DoPreview must not block.</b> Rendering needs the UI thread's message
/// pump — WebView2 initialisation completes on a posted callback — so waiting for
/// a render here would stall the host for seconds. <see cref="DoPreview"/>
/// therefore queues the work and returns <c>S_OK</c> immediately; the preview
/// appears a moment later. This is the documented expectation for preview
/// handlers, which are told to render asynchronously and keep the host
/// responsive.</para>
/// </remarks>
[ComVisible(true)]
[Guid("5B54A6AB-8765-4A71-8732-EA187093A239")]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("MarkdownPreviewer.MarkdownPreviewHandler")]
public sealed class MarkdownPreviewHandler :
    IPreviewHandler,
    IPreviewHandlerVisuals,
    IInitializeWithFile,
    IInitializeWithStream,
    IObjectWithSite,
    IOleWindow,
    IDisposable
{
    private readonly IDiagnosticLog _log = PreviewComposition.Log;
    private readonly FileMarkdownSourceReader _fileReader = new();
    private readonly StreamMarkdownSourceReader _streamReader = new();

    private PreviewUiThread? _uiThread;
    private PreviewHostWindow? _window;
    private PreviewSession? _session;
    private CancellationTokenSource? _lifetime;
    private IntPtr _windowHandle;

    private object? _site;
    private IPreviewHandlerFrame? _frame;

    private IntPtr _parentHandle;
    private Rectangle _bounds;
    private uint? _hostBackgroundColour;

    private IMarkdownSourceReader? _activeReader;
    private bool _disposed;

    public MarkdownPreviewHandler()
    {
        // Verify the CLSID literal on this class matches the one the installer
        // writes. They are declared in different assemblies for good reasons
        // (the shell layer owns the attribute, infrastructure owns the registry
        // contract) and drifting apart would produce an extension that registers
        // but never loads.
        System.Diagnostics.Debug.Assert(
            string.Equals(
                RegistryKeys.PreviewHandlerClsid,
                $"{{{GetType().GUID.ToString().ToUpperInvariant()}}}",
                StringComparison.OrdinalIgnoreCase),
            "RegistryKeys.PreviewHandlerClsid disagrees with the [Guid] on MarkdownPreviewHandler.");

        _log.Info("Markdown preview handler created.");
    }

    // ============================================================ IPreviewHandler

    public int SetWindow(IntPtr hwnd, ref RECT rect)
    {
        // Copied out of the ref parameter: a lambda cannot capture `ref`.
        Rectangle bounds = rect.ToRectangle();

        return Guard(nameof(SetWindow), () =>
        {
            _parentHandle = hwnd;
            _bounds = bounds;

            if (_window is not null && hwnd != IntPtr.Zero)
            {
                _uiThread!.Invoke(() => _window.AttachTo(hwnd, bounds));
            }

            return HResult.Ok;
        });
    }

    public int SetRect(ref RECT rect)
    {
        Rectangle bounds = rect.ToRectangle();
        return Guard(nameof(SetRect), () =>
        {
            _bounds = bounds;

            if (_window is not null)
            {
                _uiThread!.Post(() => _window?.Resize(bounds));
            }

            return HResult.Ok;
        });
    }

    public int DoPreview()
    {
        return Guard(nameof(DoPreview), () =>
        {
            if (_activeReader is null || !_activeReader.HasSource)
            {
                _log.Warn("DoPreview was called before initialisation.");
                return HResult.Unexpected;
            }

            if (_parentHandle == IntPtr.Zero)
            {
                _log.Warn("DoPreview was called before SetWindow.");
                return HResult.Unexpected;
            }

            EnsureWindowAndSession();

            // Fire and forget, on purpose — queued onto the UI thread, whose
            // WinForms context keeps every await in the pipeline there. See the
            // remarks on this type.
            IMarkdownSourceReader reader = _activeReader;
            _uiThread!.Post(() => _ = RenderAsync(reader));
            return HResult.Ok;
        });
    }

    public int Unload()
    {
        return Guard(nameof(Unload), () =>
        {
            _fileReader.Reset();
            _streamReader.Reset();
            _activeReader = null;

            if (_uiThread is not null)
            {
                _uiThread.Post(() =>
                {
                    _ = _session?.UnloadAsync(CancellationToken.None);

                    if (_window is not null)
                    {
                        _window.Visible = false;
                    }
                });
            }

            return HResult.Ok;
        });
    }

    public int SetFocus()
    {
        return Guard(nameof(SetFocus), () =>
        {
            if (_window is null)
            {
                return HResult.False;
            }

            _uiThread!.Post(() => _session?.FocusDocument());
            return HResult.Ok;
        });
    }

    public int QueryFocus(out IntPtr focusedWindow)
    {
        focusedWindow = IntPtr.Zero;

        try
        {
            // GetFocus answers for the calling thread's message queue, and the
            // preview's focus lives on the UI thread — ask over there.
            IntPtr focus = IntPtr.Zero;
            if (_uiThread is not null)
            {
                _uiThread.Invoke(() => focus = NativeMethods.GetFocus());
            }
            else
            {
                focus = NativeMethods.GetFocus();
            }

            focusedWindow = focus;
            return focusedWindow == IntPtr.Zero
                ? HResult.Fail
                : HResult.Ok;
        }
        catch (Exception ex)
        {
            _log.Error("QueryFocus failed.", ex);
            return HResult.Fail;
        }
    }

    /// <summary>
    /// Offers a keystroke to the host.
    /// </summary>
    /// <remarks>
    /// We handle no accelerators ourselves — WebView2 owns scrolling and Ctrl+C —
    /// so the correct behaviour is to forward to the host frame and return its
    /// result, falling back to <c>S_FALSE</c> ("not handled") when there is no
    /// frame. Returning <c>S_OK</c> here instead would silently swallow the
    /// host's own shortcuts.
    /// </remarks>
    public int TranslateAccelerator(ref MSG message)
    {
        try
        {
            if (_frame is null)
            {
                return HResult.False;
            }

            return _frame.TranslateAccelerator(ref message);
        }
        catch (Exception ex)
        {
            _log.Debug($"TranslateAccelerator failed: {ex.Message}");
            return HResult.False;
        }
    }

    // ==================================================== IPreviewHandlerVisuals

    public int SetBackgroundColor(uint color)
    {
        return Guard(nameof(SetBackgroundColor), () =>
        {
            _hostBackgroundColour = color;

            // Only meaningful once something is on screen; a repaint under the
            // new theme is cheap and avoids a mismatched frame.
            if (_session is not null)
            {
                _uiThread!.Post(() => _ = _session?.RefreshThemeAsync(CancellationToken.None));
            }

            return HResult.Ok;
        });
    }

    public int SetTextColor(uint color)
    {
        // github-markdown-css owns text colour; accepting and ignoring is correct
        // and keeps the host from treating us as broken.
        _ = color;
        return HResult.Ok;
    }

    public int SetFont(ref LOGFONT logFont)
    {
        // Likewise: the document's typography is defined by the stylesheet. Font
        // size is user-controlled through the FontScalePercent setting.
        return HResult.Ok;
    }

    // ======================================================= initialisation

    public int Initialize(string filePath, uint mode)
    {
        return Guard(nameof(IInitializeWithFile), () =>
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return HResult.InvalidArgument;
            }

            _fileReader.SetFile(filePath);
            _streamReader.Reset();
            _activeReader = _fileReader;

            _log.Info($"Initialised from file: {filePath}");
            return HResult.Ok;
        });
    }

    public int Initialize(IStream stream, uint mode)
    {
        return Guard(nameof(IInitializeWithStream), () =>
        {
            if (stream is null)
            {
                return HResult.InvalidArgument;
            }

            _streamReader.SetStream(stream);
            _fileReader.Reset();
            _activeReader = _streamReader;

            _log.Info("Initialised from a stream; relative images will not resolve.");
            return HResult.Ok;
        });
    }

    // ========================================================= IObjectWithSite

    public int SetSite(object? site)
    {
        return Guard(nameof(SetSite), () =>
        {
            ReleaseFrame();
            _site = site;

            // The host's frame is how accelerators get back to it. Absent in some
            // hosts, which is not an error.
            _frame = site as IPreviewHandlerFrame;
            return HResult.Ok;
        });
    }

    /// <summary>
    /// Returns the site under the requested interface.
    /// </summary>
    /// <remarks>
    /// Answered directly rather than by delegating to
    /// <c>Marshal.QueryInterface</c>: that method's IID parameter is declared
    /// <c>in Guid</c> from .NET 7 onward and <c>ref Guid</c> before, so calling it
    /// from here would bind differently across target frameworks. We only ever
    /// hold two interfaces on the site, so answering for those two and returning
    /// E_NOINTERFACE otherwise is both correct and stable.
    /// </remarks>
    public int GetSite(ref Guid riid, out object? site)
    {
        site = null;
        Guid requested = riid;   // copy out of the ref before doing anything else

        try
        {
            if (_site is null)
            {
                return HResult.Fail;
            }

            if (requested == typeof(IPreviewHandlerFrame).GUID)
            {
                if (_frame is null)
                {
                    return HResult.NoInterface;
                }

                site = _frame;
                return HResult.Ok;
            }

            if (requested == IidIUnknown)
            {
                site = _site;
                return HResult.Ok;
            }

            return HResult.NoInterface;
        }
        catch (Exception ex)
        {
            _log.Error("GetSite failed.", ex);
            return HResult.NoInterface;
        }
    }

    /// <summary>IID_IUnknown.</summary>
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    // ============================================================== IOleWindow

    public int GetWindow(out IntPtr hwnd)
    {
        // The handle is cached when the window is created on the UI thread:
        // Control.Handle is not safe to read from this (RPC) thread, and an
        // IntPtr read is atomic.
        hwnd = _windowHandle;
        return hwnd == IntPtr.Zero ? HResult.Fail : HResult.Ok;
    }

    public int ContextSensitiveHelp(bool enterMode) => HResult.NotImplemented;

    // ================================================================ internals

    private void EnsureWindowAndSession()
    {
        _uiThread ??= new PreviewUiThread(_log);

        // Synchronous on purpose: the shell may call GetWindow the moment
        // DoPreview returns, so the window (and its cached handle) must exist
        // before this method does.
        IntPtr parent = _parentHandle;
        Rectangle bounds = _bounds;
        _uiThread.Invoke(() =>
        {
            _window ??= new PreviewHostWindow();
            _window.AttachTo(parent, bounds);
            _windowHandle = _window.Handle;

            _session ??= PreviewComposition.CreateSession(_window, () => _hostBackgroundColour);
        });

        _lifetime ??= new CancellationTokenSource();
    }

    private async Task RenderAsync(IMarkdownSourceReader reader)
    {
        try
        {
            await _session!.PreviewAsync(reader, _lifetime!.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // PreviewSession already converts content failures into outcomes, so
            // reaching here means something structural went wrong.
            _log.Error("The preview pipeline faulted.", ex);
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/>, converting any exception to an HRESULT.
    /// </summary>
    private int Guard(string operation, Func<int> body)
    {
        if (_disposed)
        {
            return HResult.Unexpected;
        }

        try
        {
            return body();
        }
        catch (ArgumentException ex)
        {
            _log.Error($"{operation} rejected its arguments.", ex);
            return HResult.InvalidArgument;
        }
        catch (OutOfMemoryException ex)
        {
            _log.Error($"{operation} ran out of memory.", ex);
            return HResult.OutOfMemory;
        }
        catch (Exception ex)
        {
            _log.Error($"{operation} failed.", ex);
            return HResult.Fail;
        }
    }

    private void ReleaseFrame()
    {
        if (_frame is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(_frame))
            {
                Marshal.ReleaseComObject(_frame);
            }
        }
        catch (ArgumentException)
        {
            // Managed test double.
        }
        finally
        {
            _frame = null;
        }
    }

    // ================================================================= disposal

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _log.Info("Disposing the Markdown preview handler.");

        try
        {
            _lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        _lifetime?.Dispose();
        _lifetime = null;

        if (_uiThread is not null)
        {
            // The session and window belong to the UI thread, so they are torn
            // down there, synchronously: the surrogate may be about to exit, and
            // an un-awaited DisposeAsync would leak the browser process. The
            // session's disposal completes without needing the pump (the surface
            // tears down synchronously), so waiting inside Invoke cannot
            // deadlock.
            try
            {
                _uiThread.Invoke(() =>
                {
                    if (_session is not null)
                    {
                        try
                        {
                            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _log.Warn("Disposing the preview session failed.", ex);
                        }

                        _session = null;
                    }

                    if (_window is not null)
                    {
                        try
                        {
                            _window.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _log.Warn("Disposing the preview window failed.", ex);
                        }

                        _window = null;
                    }
                });
            }
            catch (Exception ex)
            {
                _log.Warn("Tearing down on the preview UI thread failed.", ex);
            }

            _uiThread.Dispose();
            _uiThread = null;
        }

        _windowHandle = IntPtr.Zero;

        _fileReader.Reset();
        _streamReader.Reset();
        _activeReader = null;

        ReleaseFrame();
        _site = null;
    }

    /*
     * Deliberately no finalizer.
     *
     * The tempting move is to add one as a backstop, since COM gives us no
     * deterministic disposal — the shell releases the CCW and never calls
     * Dispose. But Dispose() here touches WinForms controls and waits on
     * DisposeAsync, and neither is legal on the finalizer thread: the controls
     * belong to the STA that created them, and blocking a finalizer risks
     * hanging the finalizer queue for the whole process.
     *
     * What actually reclaims the resources is prevhost.exe itself. The shell
     * terminates the surrogate once the preview pane goes idle, which tears down
     * the WebView2 browser process with it. Unload() already releases the
     * per-document state in the meantime.
     *
     * IDisposable is kept for the harness and the tests, which own the lifetime
     * explicitly and call it on the right thread.
     */
}
