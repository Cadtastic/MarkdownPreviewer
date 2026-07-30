using System.Windows.Forms;
using MarkdownPreviewer.Application.Abstractions;

namespace MarkdownPreviewer.Shell.Hosting;

/// <summary>
/// The dedicated STA thread that owns the preview window, the session, and the
/// WebView2 browser.
/// </summary>
/// <remarks>
/// <para>Registering the class with <c>ThreadingModel = Apartment</c> is necessary
/// but not sufficient. Managed CCWs are apartment-agile — the runtime hands them
/// across apartments without proxies — so once <c>prevhost.exe</c> marshals our
/// <c>IPreviewHandler</c> back to the shell, the incoming calls are dispatched on
/// arbitrary RPC worker threads: not an STA, no guarantee <c>CoInitialize</c> ever
/// ran, and nothing pumping messages. WebView2 refuses to start on such a thread
/// (<c>CO_E_NOTINITIALIZED</c> or <c>RPC_E_CHANGED_MODE</c>), and a WinForms
/// synchronisation context installed there posts continuations to a window that is
/// never pumped, so they silently queue forever.</para>
///
/// <para>The cure is the classic managed preview-handler shape: one STA thread per
/// handler running a real message loop, with every window and browser touch
/// marshalled onto it. The COM entry points stay on whatever thread the RPC
/// runtime chose and only ever hop over here.</para>
///
/// <para>The thread starts lazily on the first <see cref="Invoke"/> or
/// <see cref="Post"/>, so a handler that is initialised and torn down without ever
/// previewing never pays for it.</para>
///
/// <para><b>One thread per process, not per handler.</b> prevhost.exe hosts
/// several handler instances over its lifetime (one per previewed document), and
/// the WebView2 environment — cached process-wide — is affine to the thread that
/// created it: a second handler with its own thread gets
/// "CoreWebView2Environment members can only be accessed from the UI thread".
/// So every handler shares <see cref="Shared"/>. The thread is a background
/// thread and is reclaimed by process exit, which is also what reclaims the
/// browser: the shell terminates the surrogate once the pane goes idle.</para>
/// </remarks>
internal sealed class PreviewUiThread(IDiagnosticLog log)
{
    private static readonly Lazy<PreviewUiThread> LazyShared =
        new(() => new PreviewUiThread(Composition.PreviewComposition.Log), isThreadSafe: true);

    /// <summary>The process-wide preview UI thread.</summary>
    public static PreviewUiThread Shared => LazyShared.Value;

    private readonly object _gate = new();

    private SynchronizationContext? _context;

    /// <summary>Runs <paramref name="work"/> on the UI thread and waits for it.</summary>
    /// <remarks>Exceptions propagate back to the caller, so callers keep their
    /// existing HRESULT conversion.</remarks>
    public void Invoke(Action work)
    {
        EnsureStarted().Send(_ => work(), null);
    }

    /// <summary>Queues <paramref name="work"/> onto the UI thread without waiting.</summary>
    /// <remarks>Exceptions are logged and swallowed: fire-and-forget work has no
    /// caller left to observe them, and nothing may throw into the surrogate.</remarks>
    public void Post(Action work)
    {
        EnsureStarted().Post(_ =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                log.Error("Queued preview work failed.", ex);
            }
        }, null);
    }

    private SynchronizationContext EnsureStarted()
    {
        lock (_gate)
        {
            if (_context is not null)
            {
                return _context;
            }

            using var ready = new ManualResetEventSlim();
            SynchronizationContext? created = null;

            var thread = new Thread(() =>
            {
                // The WinForms context makes every await in the render pipeline
                // resume on this thread; Application.Run supplies the pump that
                // WebView2 initialisation and input depend on.
                var context = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(context);
                created = context;
                ready.Set();

                System.Windows.Forms.Application.Run();

                context.Dispose();
            })
            {
                Name = "Markdown preview UI",
                IsBackground = true,
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            _context = created;
            log.Debug($"Started the preview UI thread (managed thread {thread.ManagedThreadId}).");
            return _context!;
        }
    }
}
