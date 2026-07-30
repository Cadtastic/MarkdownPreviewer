using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Domain.Rendering;

namespace MarkdownPreviewer.Application.Preview;

/// <summary>
/// Orchestrates one preview: read the document, resolve theme and settings, hand
/// it to the surface, report what happened.
/// </summary>
/// <remarks>
/// This is the application layer's single use case. It is deliberately the only
/// place that knows the ordering of those steps, and it depends on nothing but
/// abstractions — which is what makes the whole flow testable without COM,
/// WebView2, or a running Explorer.
///
/// Cancellation matters more here than in most code. Explorer fires selection
/// changes faster than a document can render, so every preview must be able to
/// abandon its predecessor cleanly.
/// </remarks>
public sealed class PreviewSession : IAsyncDisposable
{
    private readonly IPreviewSurface _surface;
    private readonly IRenderSettingsProvider _settingsProvider;
    private readonly IThemeProvider _themeProvider;
    private readonly IDiagnosticLog _log;

    private CancellationTokenSource? _inFlight;
    private RenderSettings _lastSettings = RenderSettings.Default;
    private bool _disposed;

    public PreviewSession(
        IPreviewSurface surface,
        IRenderSettingsProvider settingsProvider,
        IThemeProvider themeProvider,
        IDiagnosticLog log)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _themeProvider = themeProvider ?? throw new ArgumentNullException(nameof(themeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Reads <paramref name="reader"/> and renders it.
    /// </summary>
    /// <returns>
    /// The outcome. Never throws for content problems — a malformed or unreadable
    /// document yields <see cref="RenderOutcome.Failure"/> so the caller can show
    /// a message instead of returning a COM error to the shell.
    /// </returns>
    public async Task<RenderOutcome> PreviewAsync(IMarkdownSourceReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!reader.HasSource)
        {
            return RenderOutcome.Failure("No document has been supplied to preview.");
        }

        using CancellationTokenSource linked = SupersedePrevious(cancellationToken);
        CancellationToken token = linked.Token;

        try
        {
            RenderSettings settings = ReadSettingsSafely();
            _lastSettings = settings;

            await _surface.InitialiseAsync(token).ConfigureAwait(true);

            Domain.Documents.MarkdownDocument document =
                await reader.ReadAsync(settings.MaximumBytes, token).ConfigureAwait(true);

            if (_log.IsEnabled(DiagnosticLevel.Debug))
            {
                string identity = document.Location.HasDirectory
                    ? document.Location.ToString()
                    : $"{document.Location.FileName} (no directory — relative images will not resolve)";
                _log.Debug($"Read '{identity}': {document.Source.Length:N0} chars, " +
                           $"truncated={document.WasTruncated}.");
            }

            if (document.WasTruncated)
            {
                _log.Warn($"Truncated '{document.Location}' at {settings.MaximumBytes:N0} bytes " +
                          $"(file is {document.OriginalByteCount:N0} bytes).");
            }

            AppearanceTheme theme = _themeProvider.GetTheme(settings);
            var request = new RenderRequest(document, theme, settings);

            RenderOutcome outcome = await _surface.RenderAsync(request, token).ConfigureAwait(true);

            LogOutcome(document.Location.FileName, outcome);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection, or the pane was closed. Expected.
            _log.Debug("Preview cancelled.");
            return RenderOutcome.Failure("Preview cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error("Preview failed.", ex);
            return RenderOutcome.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Re-applies the theme to the document already on screen.
    /// </summary>
    public async Task RefreshThemeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            AppearanceTheme theme = _themeProvider.GetTheme(_lastSettings);
            await _surface.ApplyThemeAsync(theme, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Theme refresh failed.", ex);
        }
    }

    /// <summary>
    /// Moves keyboard focus into the rendered document.
    /// </summary>
    public void FocusDocument()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _surface.MoveFocusToDocument();
        }
        catch (Exception ex)
        {
            _log.Debug($"Focusing the document failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Abandons the current document. Called from <c>IPreviewHandler.Unload</c>.
    /// </summary>
    public async Task UnloadAsync(CancellationToken cancellationToken)
    {
        CancelInFlight();

        try
        {
            await _surface.ClearAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Clearing the preview surface failed.", ex);
        }
    }

    private CancellationTokenSource SupersedePrevious(CancellationToken cancellationToken)
    {
        CancelInFlight();
        CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inFlight = linked;
        return linked;
    }

    private void CancelInFlight()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _inFlight, null);
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed and disposed by its own using-block. Benign.
        }
    }

    private RenderSettings ReadSettingsSafely()
    {
        try
        {
            return _settingsProvider.GetSettings().Normalised();
        }
        catch (Exception ex)
        {
            _log.Warn("Could not read settings; falling back to defaults.", ex);
            return RenderSettings.Default;
        }
    }

    private void LogOutcome(string fileName, RenderOutcome outcome)
    {
        if (!outcome.Succeeded)
        {
            _log.Error($"Render of '{fileName}' failed: {outcome.FailureMessage}");
            return;
        }

        foreach (string warning in outcome.Warnings)
        {
            _log.Warn($"'{fileName}': {warning}");
        }

        if (_log.IsEnabled(DiagnosticLevel.Information))
        {
            _log.Info($"Rendered '{fileName}' in {outcome.Elapsed.TotalMilliseconds:F0} ms " +
                      $"(mermaid={outcome.UsedMermaid}, math={outcome.UsedMath}).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelInFlight();
        await _surface.DisposeAsync().ConfigureAwait(false);
    }
}
