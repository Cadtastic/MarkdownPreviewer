using System.Drawing;
using System.Runtime.InteropServices;
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
/// <c>assets.mdpreview.invalid</c> via a folder mapping; the previewed
/// document's own folder is <c>doc.mdpreview.invalid</c>, answered by
/// <see cref="OnWebResourceRequested"/> because a folder mapping added after the
/// page has committed never applies to it. The <c>.invalid</c> TLD is reserved
/// by RFC 2606 and can never resolve in DNS, so if a host is ever unhandled the
/// request fails locally and instantly instead of leaking a lookup onto the
/// network.</para>
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
    private readonly IDocumentRevealer _revealer;
    private readonly ITaskListEditor _taskEditor;
    private readonly ITrustedDocumentStore _trust;
    private readonly IDiagnosticLog _log;

    private WebView2? _webView;
    private FallbackMessageView? _fallback;

    private TaskCompletionSource<bool>? _pageReady;
    private TaskCompletionSource<RenderOutcome>? _pendingRender;

    private long _renderToken;
    private string? _mappedDocumentDirectory;
    private RenderRequest? _lastRequest;

    /// <summary>
    /// Whether the current document carries the user's trust grant. Read on the
    /// request path by <see cref="OnWebResourceRequested"/>, so it is refreshed
    /// per render rather than looked up per image.
    /// </summary>
    private bool _documentTrusted;

    /// <summary>
    /// Bumped whenever the document being previewed actually changes identity,
    /// as opposed to the same document being drawn again for a theme flip or a
    /// trust change. The page uses it to decide what belongs to the document
    /// and what belongs to the session — the search query, for one.
    /// </summary>
    private long _documentGeneration;

    private DocumentLocation? _lastLocation;
    private bool _initialising;
    private bool _initialised;
    private bool _unusable;
    private bool _disposed;

    public WebView2PreviewSurface(
        Control host,
        IWebAssetCatalog assets,
        IExternalLinkLauncher launcher,
        IDocumentRevealer revealer,
        ITaskListEditor taskEditor,
        ITrustedDocumentStore trust,
        IDiagnosticLog log)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _revealer = revealer ?? throw new ArgumentNullException(nameof(revealer));
        _taskEditor = taskEditor ?? throw new ArgumentNullException(nameof(taskEditor));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
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

            _pageReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            WebView2 webView = NewWebView();
            try
            {
                CoreWebView2Environment environment =
                    await WebView2EnvironmentProvider.GetAsync(_log, cancellationToken).ConfigureAwait(true);
                await webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x8007139F /* ERROR_INVALID_STATE */)
            {
                // The cached environment outlived its browser process — an
                // Evergreen update swapped the runtime, or the shared browser
                // died while no preview was open to see it go. A fresh
                // environment resolves the runtime anew; one retry is the fix,
                // not a loop: if a fresh environment also fails, something
                // bigger is wrong and the error should surface.
                _log.Warn("The cached WebView2 environment is stale (0x8007139F); retrying with a fresh one.", ex);
                WebView2EnvironmentProvider.Discard();

                _host.Controls.Remove(webView);
                webView.Dispose();
                webView = NewWebView();

                CoreWebView2Environment fresh =
                    await WebView2EnvironmentProvider.GetAsync(_log, cancellationToken).ConfigureAwait(true);
                await webView.EnsureCoreWebView2Async(fresh).ConfigureAwait(true);
            }

            HardenSettings(webView.CoreWebView2);
            WireEvents(webView.CoreWebView2);

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AssetHost,
                _assets.WebRootPath,
                CoreWebView2HostResourceAccessKind.DenyCors);

            // Every image request goes through OnWebResourceRequested. The
            // document host is answered by hand there (a folder mapping added
            // after index.html has committed never applies to the already-loaded
            // document, and the document folder changes with every selection),
            // and remote hosts are refused unless the user opted in — either
            // through the standing AllowRemoteImages preference or by trusting
            // this particular document. The CSP's img-src is deliberately broad
            // so that THIS is the enforcement point.
            //
            // The filter is Image-scoped, which is the whole of what trust can
            // widen: script, style, font and XHR contexts never reach here, and
            // CSP refuses them regardless.
            webView.CoreWebView2.AddWebResourceRequestedFilter(
                "*", CoreWebView2WebResourceContext.Image);

            _log.Info($"Navigating the preview surface to {PageUrl}.");
            webView.CoreWebView2.Navigate(PageUrl);

            await WaitForPageReadyAsync(cancellationToken).ConfigureAwait(true);
            _initialised = true;

            // If this initialisation is a rebuild after a browser death, a
            // fallback notice from the failure is still on screen.
            _fallback?.Hide();
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

    /// <summary>Creates the WebView2 control, parents it, and records it.</summary>
    private WebView2 NewWebView()
    {
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
        return webView;
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
        core.WebResourceRequested += OnWebResourceRequested;
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

        // Re-read per render: Explorer reuses this surface across selections, so
        // the previous document's grant must not carry over to the next one.
        _documentTrusted = _trust.IsTrusted(request.Document.Location.FullPath);

        // Same reuse, different consequence: the page cannot tell a new
        // selection from a redraw of the current one, because both arrive as a
        // render message. DocumentLocation compares by path, case-insensitively.
        if (!request.Document.Location.Equals(_lastLocation))
        {
            _lastLocation = request.Document.Location;
            _documentGeneration++;
        }

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
            DocumentName = request.Document.Location.FileName,
            DocumentGeneration = _documentGeneration,
            TaskEditable = request.Document.Location.HasDirectory && !request.Document.WasTruncated,
            Trusted = _documentTrusted,
            Trustable = request.Document.Location.HasDirectory,
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
        AllowRemoteImages = settings.AllowRemoteImages,
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
    /// Not a <c>SetVirtualHostNameToFolderMapping</c> call, deliberately: a
    /// folder mapping added after <c>index.html</c> has committed never applies
    /// to the already-loaded document, and this pipeline keeps one page alive
    /// across selections. The doc host is served by
    /// <see cref="OnWebResourceRequested"/>, which reads this field per request
    /// — so a directory switch takes effect instantly, with no re-navigation.
    /// When the document has no folder (stream-fed items) requests 404 locally
    /// and render as the broken-image marker — the intended degradation.
    /// </remarks>
    private void ApplyDocumentHostMapping(DocumentLocation location)
    {
        string? directory = location.HasDirectory ? location.DirectoryPath : null;

        if (!string.Equals(directory, _mappedDocumentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _log.Debug($"Document host now serves: {directory ?? "<nothing>"}.");
        }

        _mappedDocumentDirectory = directory;
    }

    /// <summary>
    /// The single gate every image request passes through: serves the document
    /// host from the current document's folder, lets the asset host fall through
    /// to its folder mapping, and blocks remote hosts unless the user opted in.
    /// </summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        CoreWebView2Environment environment = _webView!.CoreWebView2.Environment;

        try
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out Uri? uri))
            {
                return;
            }

            if (string.Equals(uri.Host, AssetHost, StringComparison.OrdinalIgnoreCase))
            {
                return;   // the folder mapping serves the page's own assets
            }

            if (string.Equals(uri.Host, DocumentHost, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(e.Request.Method, "GET", StringComparison.OrdinalIgnoreCase) ||
                    !TryMapDocumentUrl(uri, out string fullPath))
                {
                    e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
                    return;
                }

                var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

                e.Response = environment.CreateWebResourceResponse(
                    stream, 200, "OK", $"Content-Type: {MimeTypeFor(Path.GetExtension(fullPath))}");
                return;
            }

            // Anything else is remote. Off by default: remote images are how
            // tracking pixels learn that this user looked at this file. Two
            // things open the gate, and only these two — the standing
            // AllowRemoteImages preference, or the user having trusted this
            // particular document through the toolbar.
            if (!_documentTrusted && _lastRequest?.Settings.AllowRemoteImages != true)
            {
                e.Response = environment.CreateWebResourceResponse(
                    null, 403, "External resources are blocked for this document", string.Empty);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Serving '{e.Request.Uri}' failed: {ex.Message}");
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
        }
    }

    /// <summary>
    /// Maps a <c>doc.mdpreview.invalid</c> URL to a file inside the current
    /// document's folder. Canonicalises and confines: "../" and absolute-path
    /// tricks must not escape the folder.
    /// </summary>
    private bool TryMapDocumentUrl(Uri uri, out string fullPath)
    {
        fullPath = string.Empty;

        string? directory = _mappedDocumentDirectory;
        if (directory is null)
        {
            return false;
        }

        string relative = Uri.UnescapeDataString(uri.AbsolutePath)
            .TrimStart('/')
            .Replace('/', Path.DirectorySeparatorChar);

        string root = Path.GetFullPath(directory + Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(root, relative));

        // Directories qualify too: a "docs/" link navigates Explorer into the
        // folder. The image-serving caller is unaffected — opening a directory
        // as a FileStream fails into its existing 404 path.
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            (!File.Exists(candidate) && !Directory.Exists(candidate)))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static string MimeTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        _ => "application/octet-stream",
    };

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

    /// <summary>
    /// Whether a web message came from our own render page.
    /// </summary>
    /// <remarks>
    /// Not a bare string comparison against <see cref="PageUrl"/>, deliberately.
    /// The page's URI gains a <c>#fragment</c> the first time the reader follows
    /// an in-page anchor — a heading link, a contents-rail entry — and WebView2
    /// reports the fragment as part of <c>Source</c>. An exact match then
    /// silently rejected every message the page sent for the rest of the page's
    /// life: trust clicks did nothing, every render died on its 30-second
    /// timeout, and the preview looked folder-dependent because it actually
    /// depended on whether the reader had clicked an anchor yet. The fragment
    /// never changes which document is talking, so it is stripped before the
    /// comparison; scheme, host and path still have to match exactly.
    /// </remarks>
    internal static bool IsFromPreviewPage(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return false;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        // GetLeftPart(Query) keeps everything up to and including any query
        // string. Our page is never served with one, so a source carrying a
        // query still fails the comparison — only the fragment is forgiven.
        return string.Equals(
            uri.GetLeftPart(UriPartial.Query), PageUrl, StringComparison.OrdinalIgnoreCase);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        PageToHostMessage? message;
        try
        {
            // Only ever trust messages that came from our own page.
            if (!IsFromPreviewPage(e.Source))
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
                if (message.Url is { Length: > 0 } url &&
                    !url.Contains(".mdpreview.invalid/", StringComparison.OrdinalIgnoreCase))
                {
                    _launcher.Launch(url);
                }

                break;

            case "imageTextRequest":
                // The page cannot see inside an <img> - an SVG loaded that way
                // is a separate document. We are already the thing serving
                // those files, so read the text out and hand it back. Only
                // document-host SVGs qualify; anything else silently yields
                // nothing rather than an error the page cannot act on.
                if (message.Urls is { Length: > 0 } requested)
                {
                    var entries = new List<ImageTextEntry>();
                    foreach (string rawUrl in requested.Take(40))
                    {
                        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? imageUri) &&
                            string.Equals(imageUri.Host, DocumentHost, StringComparison.OrdinalIgnoreCase) &&
                            imageUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) &&
                            TryMapDocumentUrl(imageUri, out string imagePath))
                        {
                            entries.Add(new ImageTextEntry
                            {
                                Url = rawUrl,
                                Text = SvgTextExtractor.Extract(imagePath),
                            });
                        }
                    }

                    if (entries.Count > 0)
                    {
                        Post(new HostToPageMessage { Kind = "imageText", Images = [.. entries] });
                    }
                }

                break;

            case "openDocument":
                // A link to a sibling of the previewed document. Resolve it back
                // to a real file, confined to the document's folder, then go the
                // way the reader chose on the toolbar:
                //
                //   navigate  Explorer is steered to the file and selects it, so
                //             the preview follows. Selecting executes nothing,
                //             so any file that exists qualifies.
                //   (else)    the file is launched in its default application.
                //             The launcher applies its inert-type allowlist on
                //             top — a document must not be one click away from
                //             running a script it shipped alongside itself.
                if (message.Url is { Length: > 0 } documentUrl &&
                    Uri.TryCreate(documentUrl, UriKind.Absolute, out Uri? documentUri) &&
                    string.Equals(documentUri.Host, DocumentHost, StringComparison.OrdinalIgnoreCase) &&
                    TryMapDocumentUrl(documentUri, out string linkedPath))
                {
                    if (string.Equals(message.Mode, "navigate", StringComparison.OrdinalIgnoreCase))
                    {
                        _revealer.Reveal(linkedPath, _host.Handle, _mappedDocumentDirectory);
                    }
                    else
                    {
                        _launcher.LaunchDocument(linkedPath);
                    }
                }
                else
                {
                    _log.Debug($"Ignored a document link that does not resolve: {message.Url}");
                }

                break;

            case "toggleTask":
                // The reader checked or unchecked a task box. The editor
                // re-reads the file and verifies the marker before flipping one
                // character; on success the in-memory document is refreshed so
                // a later redraw (theme flip, trust change) shows the new state
                // instead of resurrecting the old one. On refusal the document
                // is re-rendered from what is actually on disk, which snaps the
                // checkbox back — silently, as designed, with the reason in the
                // log.
                if (_lastRequest is { } editTarget &&
                    editTarget.Document.Location is { HasDirectory: true, FullPath: { } editPath } &&
                    !editTarget.Document.WasTruncated &&
                    message.Line is >= 0 and <= int.MaxValue)
                {
                    if (_taskEditor.TryToggle(editPath, (int)message.Line, message.Checked, out string updatedText))
                    {
                        // OriginalByteCount is only read to compose the
                        // truncation notice, which never applies to an editable
                        // document, so carrying the old value forward is safe.
                        _lastRequest = new RenderRequest(
                            new MarkdownDocument(
                                updatedText,
                                editTarget.Document.Location,
                                wasTruncated: false,
                                editTarget.Document.OriginalByteCount),
                            editTarget.Theme,
                            editTarget.Settings);
                    }
                    else
                    {
                        _ = RenderAsync(editTarget, CancellationToken.None);
                    }
                }
                else
                {
                    _log.Debug("Ignoring a task toggle for an item that cannot be edited.");
                }

                break;

            case "trustDocument":
                // The page has already put its warning in front of the user and
                // taken a deliberate answer; this is the recorded result, not
                // the request. Re-render afterwards so the resources that were
                // refused a moment ago are fetched (or, on withdrawal, dropped).
                if (_lastRequest is { } trustTarget)
                {
                    string? path = trustTarget.Document.Location.FullPath;

                    if (trustTarget.Document.Location.HasDirectory)
                    {
                        _trust.SetTrusted(path, message.Trusted);
                        _documentTrusted = _trust.IsTrusted(path);
                        _ = RenderAsync(trustTarget, CancellationToken.None);
                    }
                    else
                    {
                        _log.Debug("Ignoring a trust change for an item with no file on disk.");
                    }
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

    /// <summary>
    /// Entries a read-only preview must not offer, wherever the browser nests
    /// them. "share" is stripped because the built-in flavour shares the page
    /// URL — a process-local virtual host that is meaningless off this machine;
    /// our own "Share document…" shares the file instead.
    /// </summary>
    private static readonly HashSet<string> StrippedMenuItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "back", "forward", "reload", "share", "webSelect", "saveAs", "saveImageAs",
        "print", "webCapture", "emoji", "inspectElement", "viewSource",

        // "Send tab to your devices". Sits at the TOP level rather than under
        // "More tools", which is how it survived the 1.1.0 cleanup: it syncs a
        // page URL to another signed-in device, and our URL is a process-local
        // virtual host that means nothing anywhere else.
        "sendTabToSelf",

        // Offered when the click lands on a link. A preview pane has no tabs or
        // windows to open into, and "save link as" is a download. Copying the
        // link address is fine and stays.
        "openLinkInNewWindow", "openLinkInNewTab", "openLinkInSplitScreenView",
        "saveLinkAs",
    };

    /// <summary>
    /// Strips menu entries that make no sense for a read-only preview, and adds
    /// the document actions and the table-of-contents toggle.
    /// </summary>
    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (_log.IsEnabled(DiagnosticLevel.Debug))
        {
            // The browser's menu varies by runtime version, so what is worth
            // stripping is a moving target. Log what actually arrived.
            _log.Debug($"Context menu offered: {DescribeMenu(e.MenuItems)}");
        }

        StripUnwantedItems(e.MenuItems);
        TidySeparators(e.MenuItems);

        try
        {
            CoreWebView2Environment environment = _webView!.CoreWebView2.Environment;

            if (e.MenuItems.Count > 0)
            {
                e.MenuItems.Add(environment.CreateContextMenuItem(
                    string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));
            }

            // Document actions only make sense when the document IS a file —
            // stream-fed items (zip members, search results) have nothing to
            // hand to another app.
            string? documentPath = _lastRequest?.Document.Location.FullPath;
            if (documentPath is not null && File.Exists(documentPath))
            {
                CoreWebView2ContextMenuItem copy = environment.CreateContextMenuItem(
                    "Copy document", null, CoreWebView2ContextMenuItemKind.Command);
                copy.CustomItemSelected += (_, _) => CopyDocumentToClipboard(documentPath);
                e.MenuItems.Add(copy);

                CoreWebView2ContextMenuItem share = environment.CreateContextMenuItem(
                    "Share document…", null, CoreWebView2ContextMenuItemKind.Command);
                share.CustomItemSelected += (_, _) =>
                    Interop.WindowsShare.ShowForFile(_host.Handle, documentPath, _log);
                e.MenuItems.Add(share);

                e.MenuItems.Add(environment.CreateContextMenuItem(
                    string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));
            }

            // No tab in the label: WebView2 renders it literally instead of
            // right-aligning an accelerator column, so it read as "Find...->Ctrl+F".
            CoreWebView2ContextMenuItem findItem = environment.CreateContextMenuItem(
                "Find… (Ctrl+F)", null, CoreWebView2ContextMenuItemKind.Command);
            findItem.CustomItemSelected += (_, _) => Post(new HostToPageMessage { Kind = "find" });
            e.MenuItems.Add(findItem);

            CoreWebView2ContextMenuItem toggle = environment.CreateContextMenuItem(
                "Toggle table of contents", null, CoreWebView2ContextMenuItemKind.Command);
            toggle.CustomItemSelected += (_, _) => Post(new HostToPageMessage { Kind = "toc" });
            e.MenuItems.Add(toggle);

            if (_log.IsEnabled(DiagnosticLevel.Debug))
            {
                _log.Debug($"Context menu shown: {DescribeMenu(e.MenuItems)}");
            }
        }
        catch (Exception ex)
        {
            // A missing menu item must never take the menu (or the preview) down.
            _log.Debug($"Adding custom menu items failed: {ex.Message}");
        }
    }

    /// <summary>Flattens the menu to a readable list for the log.</summary>
    private static string DescribeMenu(IList<CoreWebView2ContextMenuItem> items) =>
        string.Join(", ", items.Select(item =>
            item.Kind == CoreWebView2ContextMenuItemKind.Submenu
                ? $"{item.Name}[{DescribeMenu(item.Children)}]"
                : item.Name));

    private static void StripUnwantedItems(IList<CoreWebView2ContextMenuItem> items)
    {
        for (int i = items.Count - 1; i >= 0; i--)
        {
            CoreWebView2ContextMenuItem item = items[i];

            if (item.Kind == CoreWebView2ContextMenuItemKind.Submenu)
            {
                // "share" lives under "More tools"; a flat scan never sees it.
                StripUnwantedItems(item.Children);
                if (item.Children.All(c => c.Kind == CoreWebView2ContextMenuItemKind.Separator))
                {
                    items.RemoveAt(i);
                }

                continue;
            }

            if (StrippedMenuItems.Contains(item.Name))
            {
                items.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Removes leading, trailing and doubled separators.
    /// </summary>
    /// <remarks>
    /// Stripping named items leaves the separators that framed them, so without
    /// this the menu opens with a rule above the first entry and shows gaps
    /// where the browser's own items used to be.
    /// </remarks>
    private static void TidySeparators(IList<CoreWebView2ContextMenuItem> items)
    {
        bool previousWasSeparator = true;   // leading separators are unwanted too

        for (int i = 0; i < items.Count; i++)
        {
            bool isSeparator = items[i].Kind == CoreWebView2ContextMenuItemKind.Separator;

            if (isSeparator && previousWasSeparator)
            {
                items.RemoveAt(i--);
                continue;
            }

            previousWasSeparator = isSeparator;
        }

        while (items.Count > 0 &&
               items[^1].Kind == CoreWebView2ContextMenuItemKind.Separator)
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    private void CopyDocumentToClipboard(string fullPath)
    {
        try
        {
            var files = new System.Collections.Specialized.StringCollection { fullPath };
            Clipboard.SetFileDropList(files);
            _log.Info($"Copied '{Path.GetFileName(fullPath)}' to the clipboard as a file.");
        }
        catch (Exception ex)
        {
            _log.Warn("Copying the document to the clipboard failed.", ex);
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
            // The whole browser is gone. The old behaviour latched this surface
            // unusable, which meant one browser death (an Evergreen update, a
            // crash) poisoned the pane until the surrogate itself died. Instead:
            // drop the dead control and the process-wide environment cache, and
            // let the next render rebuild from scratch.
            _log.Warn("Discarding the dead browser surface; the next selection rebuilds it.");
            WebView2EnvironmentProvider.Discard();

            _initialised = false;
            _pageReady = null;

            if (_webView is not null)
            {
                try
                {
                    _host.Controls.Remove(_webView);
                    _webView.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Debug($"Disposing the dead WebView2 control failed: {ex.Message}");
                }

                _webView = null;
            }

            _fallback ??= CreateFallback();
            _fallback.Show(
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
                    core.WebResourceRequested -= OnWebResourceRequested;
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
