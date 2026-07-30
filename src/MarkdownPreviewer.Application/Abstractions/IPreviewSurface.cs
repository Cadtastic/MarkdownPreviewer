using MarkdownPreviewer.Domain.Rendering;

namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>
/// The thing that actually paints a document — in production, a WebView2 host.
/// </summary>
/// <remarks>
/// Kept deliberately narrow so the use case can be tested against a fake, and so
/// swapping WebView2 for something else does not reach into the application layer.
/// </remarks>
public interface IPreviewSurface : IAsyncDisposable
{
    /// <summary>
    /// Prepares the surface. Safe to call repeatedly; only the first call does work.
    /// </summary>
    Task InitialiseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Renders a document, superseding any render still in flight.
    /// </summary>
    Task<RenderOutcome> RenderAsync(RenderRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Repaints the current document under a new theme without re-reading the file.
    /// </summary>
    Task ApplyThemeAsync(AppearanceTheme theme, CancellationToken cancellationToken);

    /// <summary>Clears the surface and releases per-document resources.</summary>
    Task ClearAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Moves keyboard focus into the rendered document.
    /// </summary>
    /// <remarks>
    /// Required by <c>IPreviewHandler.SetFocus</c>: when the user tabs into the
    /// preview pane the shell expects the handler to take focus so that scrolling
    /// and text selection work. Synchronous because the shell calls it that way.
    /// </remarks>
    void MoveFocusToDocument();
}
