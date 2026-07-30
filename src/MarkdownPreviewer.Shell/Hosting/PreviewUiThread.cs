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
/// </remarks>
internal sealed class PreviewUiThread(IDiagnosticLog log) : IDisposable
{
    private readonly object _gate = new();

    private Thread? _thread;
    private SynchronizationContext? _context;
    private volatile bool _disposed;

    /// <summary>Runs <paramref name="work"/> on the UI thread and waits for it.</summary>
    /// <remarks>Exceptions propagate back to the caller, so callers keep their
    /// existing HRESULT conversion.</remarks>
    public void Invoke(Action work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureStarted().Send(_ => work(), null);
    }

    /// <summary>Queues <paramref name="work"/> onto the UI thread without waiting.</summary>
    /// <remarks>Exceptions are logged and swallowed: fire-and-forget work has no
    /// caller left to observe them, and nothing may throw into the surrogate.</remarks>
    public void Post(Action work)
    {
        if (_disposed)
        {
            return;
        }

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

            _thread = new Thread(() =>
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

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();

            _context = created;
            log.Debug("Started the preview UI thread.");
            return _context!;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SynchronizationContext? context;
        Thread? thread;
        lock (_gate)
        {
            context = _context;
            thread = _thread;
            _context = null;
            _thread = null;
        }

        if (context is null || thread is null)
        {
            return;
        }

        context.Post(_ => System.Windows.Forms.Application.ExitThread(), null);

        // Bounded: the surrogate may be about to exit, and a wedged message loop
        // must not stop it. A background thread cannot hold the process open.
        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            log.Warn("The preview UI thread did not exit within 5 seconds.");
        }
    }
}
