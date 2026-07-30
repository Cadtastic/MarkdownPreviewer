using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Domain.Documents;
using MarkdownPreviewer.Domain.Rendering;
using MarkdownPreviewer.Rendering.Messaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MarkdownPreviewer.Rendering.WebView;

/// <summary>
/// Renders Markdown by driving a WebView2 instance that hosts the bundled
/// markdown-it / highlight.js / mermaid / MathJax page.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Every member must be called on the STA thread that owns
/// <see cref="Control.Handle"/> of the host control — the dedicated, pumped UI
/// thread the host provides (the shell handler's <c>PreviewUiThread</c>, the
/// harness's WinForms main thread). It is never the thread the shell calls
/// <c>IPreviewHandler</c> on: those calls arrive on unpumped RPC worker threads.
/// All awaits use <c>ConfigureAwait(true)</c> so continuations come back to the
/// WinForms synchronisation context; WebView2 will throw if touched from anywhere
/// else.</para>
///
/// <para><b>Ordering.</b> Explorer changes selection faster than a document with a
/// diagram in it can render. Each render carries a monotonic token; a reply whose
/// token is stale is dropped rather than applied.</para>
///
/// <para><b>Virtual hosts.</b> The page is served from
/// <c>assets.mdpreview.invalid</c> and the previewed document's own folder from
/// <c>doc.mdpreview.invalid</c>. The <c>.invalid</c> TLD is reserved by RFC 2606
/// and can never resolve in DNS, so if a mapping is ever missing the request fails
/// locally and instantly instead of leaking a lookup onto the network.</para>
/// </remarks>
public sealed class WebView2PreviewSurface : IPreviewSurface
{
    private const string AssetHost = "assets.mdpreview.invalid";
    private const string DocumentHost = "doc.mdpreview.invalid";
    private const string PageUrl = "https://" + AssetHost + "/index.html";

    /// <summary>
    /// Upper bound on one render. Generous because a first mermaid render pays for
    /// a 3.5 MB script parse, but finite because a script error on the page would
    /// otherwise leave the shell waiting forever.
    /// </summary>
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PageReadyTimeout = TimeSpan.FromSeconds(20);

    private readonly Control _host;
    private readonly IWebAssetCatalog _assets;
    private readonly IExternalLinkLauncher _launcher;
    private readonly IDiagnosticLog _log;

    private WebView2? _webView;
    private FallbackMessageView? _fallback;

    private TaskCompletionSource<bool>? _pageReady;
    private TaskCompletionSource<RenderOutcome>? _pendingRender;

    private long _renderToken;
    private string? _mappedDocumentDirectory;
    private RenderRequest? _lastRequest;
    private bool _initialising;
    private bool _initialised;
    private bool _unusable;
    private bool _disposed;

    public WebView2PreviewSurface(
        Control host,
        IWebAssetCatalog assets,
        IExternalLinkLauncher launcher,
        IDiagnosticLog log)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // ------------------------------------------------------------ lifecycle ---

    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialised || _unusable)
        {
            return;
        }

        if (_initialising)
        {
            // A second selection arrived while the first was still bringing the
            // browser up. Wait for that attempt rather than starting another.
            await WaitForPageReadyAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        _initialising = true;
        try
        {
            if (!_assets.IsValid)
            {
                MarkUnusable(
                    "Markdown preview could not start.",
                    $"Render assets are missing from{Environment.NewLine}{_assets.WebRootPath}" +
                    $"{Environment.NewLine}{Environment.NewLine}Missing: {string.Join(", ", _assets.MissingFiles)}" +
                    $"{Environment.NewLine}{Environment.NewLine}Reinstalling the Markdown Preview Handler should fix this.");
                return;
            }

            CoreWebView2Environment environment =
                await WebView2EnvironmentProvider.GetAsync(_log, cancellationToken).ConfigureAwait(true);

            var webView = new WebView2
            {
                Dock = DockStyle.Fill,
                // Painted before the page loads; matching it to the theme avoids a
                // white flash when the pane is dark.
                DefaultBackgroundColor = Color.White,
                AllowExternalDrop = false,
                TabStop = true,
            };

            _host.Controls.Add(webView);
            _webView = webView;

            _pageReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

            HardenSettings(webView.CoreWebView2);
            WireEvents(webView.CoreWebView2);

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AssetHost,
                _assets.WebRootPath,
                CoreWebView2HostResourceAccessKind.DenyCors);

            _log.Info($"Navigating the preview surface to {PageUrl}.");
            webView.CoreWebView2.Navigate(PageUrl);

            await WaitForPageReadyAsync(cancellationToken).ConfigureAwait(true);
            _initialised = true;
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            _log.Error("The WebView2 runtime is not installed.", ex);
            MarkUnusable(
                "Markdown preview needs the Microsoft Edge WebView2 runtime.",
                "Install the Evergreen WebView2 Runtime from Microsoft, then close and reopen " +
                "File Explorer.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Initialising the WebView2 preview surface failed.", ex);
            MarkUnusable("Markdown preview could not start.", ex.Message);
        }
        finally
        {
            _initialising = false;
        }
    }

    private async Task WaitForPageReadyAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool>? ready = _pageReady;
        if (ready is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(PageReadyTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await ready.Task.WaitAsync(linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The render page did not signal readiness within {PageReadyTimeout.TotalSeconds:F0} s.");
        }
    }

    /// <summary>
    /// Turns off everything a document previewer has no business doing.
    /// </summary>
    /// <remarks>
    /// The user selected a file; they did not agree to run it. Autofill, password
    /// saving, host objects and script dialogs are all attack surface with zero
    /// upside here. Default context menus stay on: being able to copy a snippet
    /// out of the preview is genuinely useful, and with DevTools disabled the menu
    /// exposes nothing sensitive.
    /// </remarks>
    private static void HardenSettings(CoreWebView2 core)
    {
        CoreWebView2Settings settings = core.Settings;

        settings.IsScriptEnabled = true;                  // required: we render client-side
        settings.IsWebMessageEnabled = true;              // required: host <-> page protocol

        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;  // no alert()/confirm() from a preview
        settings.AreDefaultContextMenusEnabled = true;    // keep copy
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsZoomControlEnabled = true;             // Ctrl+scroll is expected behaviour
        settings.IsPinchZoomEnabled = true;
    }

    private void WireEvents(CoreWebView2 core)
    {
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;
        core.DocumentTitleChanged += (_, _) => { /* no chrome to update; kept for tracing */ };
        core.ContextMenuRequested += OnContextMenuRequested;
        core.PermissionRequested += OnPermissionRequested;
    }

    // --------------------------------------------------------------- render ---

    public async Task<RenderOutcome> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await InitialiseAsync(cancellationToken).ConfigureAwait(true);

        if (_unusable || _webView?.CoreWebView2 is null)
        {
            return RenderOutcome.Failure("The preview surface is unavailable.");
        }

        _lastRequest = request;
        ApplyDocumentHostMapping(request.Document.Location);
        ApplyBackgroundColour(request.Theme);

        long token = Interlocked.Increment(ref _renderToken);

        // Supersede any render still awaiting a reply so its caller unblocks.
        _pendingRender?.TrySetResult(RenderOutcome.Failure("Superseded by a newer selection."));
        var completion = new TaskCompletionSource<RenderOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRender = completion;

        var message = new HostToPageMessage
        {
            Kind = "render",
            Token = token,
            Markdown = ComposeSource(request.Document),
            Theme = request.Theme == AppearanceTheme.Dark ? "dark" : "light",
            DocBase = "https://" + DocumentHost + "/",
            Settings = Project(request.Settings),
        };

        Post(message);

        using var timeout = new CancellationTokenSource(RenderTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _log.Error($"Render token {token} timed out after {RenderTimeout.TotalSeconds:F0} s.");
            return RenderOutcome.Failure("Rendering took too long and was abandoned.");
        }
        finally
        {
            if (ReferenceEquals(_pendingRender, completion))
            {
                _pendingRender = null;
            }
        }
    }

    /// <summary>
    /// Prepends a visible notice when the document was truncated, so the user is
    /// never silently shown a partial file.
    /// </summary>
    private static string ComposeSource(MarkdownDocument document)
    {
        if (!document.WasTruncated)
        {
            return document.Source;
        }

        string notice =
            $"> **Preview truncated.** Showing the first part of a " +
            $"{document.OriginalByteCount / 1024.0 / 1024.0:F1} MB file.\n\n";

        return notice + document.Source;
    }

    private static PageSettings Project(RenderSettings settings) => new()
    {
        AllowRawHtml = settings.AllowRawHtml,
        Linkify = settings.Linkify,
        Typographer = settings.Typographer,
        Highlight = settings.Highlight,
        Mermaid = settings.Mermaid,
        Math = settings.Math,
        SingleDollarMath = settings.SingleDollarMath,
        TaskLists = settings.TaskLists,
        ShowFrontMatter = settings.ShowFrontMatter,
        FontScalePercent = settings.FontScalePercent,
    };

    /// <summary>
    /// Points <c>doc.mdpreview.invalid</c> at the current document's folder.
    /// </summary>
    /// <remarks>
    /// Remapped only when the folder actually changes: browsing a folder of
    /// Markdown files would otherwise tear down and rebuild the mapping on every
    /// arrow-key press. When the document has no folder (stream initialisation)
    /// the mapping is cleared, and relative images resolve to an unmapped
    /// <c>.invalid</c> host, which fails locally and renders as a broken-image
    /// marker — the intended degradation.
    /// </remarks>
    private void ApplyDocumentHostMapping(DocumentLocation location)
    {
        CoreWebView2 core = _webView!.CoreWebView2;
        string? directory = location.HasDirectory ? location.DirectoryPath : null;

        if (string.Equals(directory, _mappedDocumentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_mappedDocumentDirectory is not null)
        {
            try
            {
                core.ClearVirtualHostNameToFolderMapping(DocumentHost);
            }
            catch (Exception ex)
            {
                _log.Debug($"Clearing the document host mapping failed: {ex.Message}");
            }
        }

        if (directory is not null)
        {
            core.SetVirtualHostNameToFolderMapping(
                DocumentHost, directory, CoreWebView2HostResourceAccessKind.DenyCors);
        }

        _mappedDocumentDirectory = directory;
    }

    // ---------------------------------------------------------------- theme ---

    public async Task ApplyThemeAsync(AppearanceTheme theme, CancellationToken cancellationToken)
    {
        if (_disposed || _unusable || _webView?.CoreWebView2 is null)
        {
            return;
        }

        ApplyBackgroundColour(theme);
        Post(new HostToPageMessage { Kind = "theme", Theme = theme == AppearanceTheme.Dark ? "dark" : "light" });

        // Mermaid bakes theme colours into the SVG it emits, so diagrams need a
        // full re-render. The page tells us when that applies; everything else
        // (CSS-driven) flips without touching the document.
        if (_lastRequest is { } previous && previous.Theme != theme)
        {
            _lastRequest = new RenderRequest(previous.Document, theme, previous.Settings);
        }

        await Task.CompletedTask.ConfigureAwait(true);
        _ = cancellationToken;
    }

    private void ApplyBackgroundColour(AppearanceTheme theme)
    {
        if (_webView is null)
        {
            return;
        }

        // Values taken from github-markdown-css canvas colours so the frame
        // matches the document instead of flashing white.
        _webView.DefaultBackgroundColor = theme == AppearanceTheme.Dark
            ? Color.FromArgb(0x0D, 0x11, 0x17)
            : Color.FromArgb(0xFF, 0xFF, 0xFF);
    }

    // ---------------------------------------------------------------- clear ---

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        // Capture the theme before discarding the request, so the blank surface
        // stays the colour the user was already looking at.
        AppearanceTheme theme = _lastRequest?.Theme ?? AppearanceTheme.Light;

        _pendingRender?.TrySetResult(RenderOutcome.Failure("Unloaded."));
        _pendingRender = null;
        _lastRequest = null;

        if (_disposed || _unusable || _webView?.CoreWebView2 is null)
        {
            return Task.CompletedTask;
        }

        // Render an empty document rather than navigating away: keeping the page
        // alive is what makes the next selection fast.
        Post(new HostToPageMessage
        {
            Kind = "render",
            Token = Interlocked.Increment(ref _renderToken),
            Markdown = string.Empty,
            Theme = theme == AppearanceTheme.Dark ? "dark" : "light",
            DocBase = "https://" + DocumentHost + "/",
            Settings = Project(RenderSettings.Default),
        });

        _ = cancellationToken;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void MoveFocusToDocument()
    {
        if (_unusable)
        {
            _fallback?.Focus();
            return;
        }

        _webView?.Focus();
    }

    // -------------------------------------------------------------- plumbing ---

    private void Post(HostToPageMessage message)
    {
        try
        {
            string json = JsonSerializer.Serialize(message, PreviewJsonContext.Default.HostToPageMessage);
            _webView!.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            _log.Error("Posting a message to the render page failed.", ex);
            _pendingRender?.TrySetResult(RenderOutcome.Failure(ex.Message));
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        PageToHostMessage? message;
        try
        {
            // Only ever trust messages that came from our own page.
            if (!string.Equals(e.Source, PageUrl, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn($"Ignoring a web message from an unexpected source: {e.Source}");
                return;
            }

            message = JsonSerializer.Deserialize(
                e.WebMessageAsJson, PreviewJsonContext.Default.PageToHostMessage);
        }
        catch (Exception ex)
        {
            _log.Warn("Could not parse a message from the render page.", ex);
            return;
        }

        if (message is null)
        {
            return;
        }

        switch (message.Kind)
        {
            case "ready":
                _log.Info($"Render page {message.Version} is ready.");
                _pageReady?.TrySetResult(true);
                break;

            case "rendered":
                CompleteRender(message, RenderOutcome.Success(
                    TimeSpan.FromMilliseconds(message.ElapsedMs),
                    message.UsedMermaid,
                    message.UsedMath,
                    message.Warnings ?? []));
                break;

            case "failed":
                CompleteRender(message, RenderOutcome.Failure(message.Message ?? "Unknown render failure."));
                break;

            case "openExternal":
                if (message.Url is { Length: > 0 } url)
                {
                    _launcher.Launch(url);
                }

                break;

            case "rerenderRequested":
                // Mermaid needs redrawing after a theme flip.
                if (_lastRequest is { } request)
                {
                    _ = RenderAsync(request, CancellationToken.None);
                }

                break;

            case "scriptError":
                _log.Error($"Render page script error: {message.Message}");
                break;

            default:
                _log.Debug($"Ignoring unknown page message kind '{message.Kind}'.");
                break;
        }
    }

    private void CompleteRender(PageToHostMessage message, RenderOutcome outcome)
    {
        if (message.Token != Interlocked.Read(ref _renderToken))
        {
            _log.Debug($"Dropping a stale render reply (token {message.Token}).");
            return;
        }

        _pendingRender?.TrySetResult(outcome);
    }

    /// <summary>
    /// Blocks every navigation except the one that loads our own page.
    /// </summary>
    /// <remarks>
    /// The page's click handler already routes links to the host, but a preview
    /// pane must not navigate even if that handler is bypassed — by a redirect, a
    /// meta refresh, or raw HTML when the user has opted into it. This is the
    /// backstop that makes those cases inert.
    /// </remarks>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.Equals(e.Uri, PageUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _log.Warn($"Blocked navigation to {e.Uri}.");
        e.Cancel = true;

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? target) &&
            (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps))
        {
            _launcher.Launch(target.AbsoluteUri);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _launcher.Launch(e.Uri);
    }

    /// <summary>Strips menu entries that make no sense for a read-only preview.</summary>
    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        for (int i = e.MenuItems.Count - 1; i >= 0; i--)
        {
            string name = e.MenuItems[i].Name;
            if (name is "back" or "forward" or "reload" or "share" or "webSelect" or
                        "saveAs" or "saveImageAs" or "print" or "webCapture" or
                        "emoji" or "inspectElement" or "viewSource")
            {
                e.MenuItems.RemoveAt(i);
            }
        }
    }

    /// <summary>Denies every capability request. A preview needs none of them.</summary>
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
        _log.Warn($"Denied a {e.PermissionKind} permission request from the preview.");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.Error($"WebView2 process failed: {e.ProcessFailedKind} ({e.Reason}), " +
                   $"exit code {e.ExitCode}.");

        _pendingRender?.TrySetResult(RenderOutcome.Failure($"The rendering process stopped ({e.ProcessFailedKind})."));

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // The whole browser is gone; this surface cannot recover. A new
            // handler instance will build a fresh one.
            _initialised = false;
            MarkUnusable(
                "Markdown preview stopped unexpectedly.",
                "Select the file again to retry.");
        }
    }

    private void MarkUnusable(string heading, string detail)
    {
        _unusable = true;
        _pageReady?.TrySetResult(false);
        _pendingRender?.TrySetResult(RenderOutcome.Failure(heading));

        if (_webView is not null)
        {
            _webView.Visible = false;
        }

        _fallback ??= CreateFallback();
        _fallback.Show(heading, detail);
    }

    private FallbackMessageView CreateFallback()
    {
        var view = new FallbackMessageView();
        _host.Controls.Add(view);
        return view;
    }

    // -------------------------------------------------------------- disposal ---

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _pendingRender?.TrySetResult(RenderOutcome.Failure("Disposed."));
        _pendingRender = null;
        _pageReady = null;

        if (_webView is not null)
        {
            try
            {
                if (_webView.CoreWebView2 is { } core)
                {
                    core.WebMessageReceived -= OnWebMessageReceived;
                    core.NavigationStarting -= OnNavigationStarting;
                    core.NewWindowRequested -= OnNewWindowRequested;
                    core.ProcessFailed -= OnProcessFailed;
                    core.ContextMenuRequested -= OnContextMenuRequested;
                    core.PermissionRequested -= OnPermissionRequested;
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"Detaching WebView2 events failed: {ex.Message}");
            }

            try
            {
                _host.Controls.Remove(_webView);
                _webView.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn("Disposing the WebView2 control failed.", ex);
            }

            _webView = null;
        }

        if (_fallback is not null)
        {
            _host.Controls.Remove(_fallback);
            _fallback.Dispose();
            _fallback = null;
        }

        return ValueTask.CompletedTask;
    }
}
