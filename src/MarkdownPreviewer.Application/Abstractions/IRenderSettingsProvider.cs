using MarkdownPreviewer.Domain.Rendering;

namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>Supplies the current user settings.</summary>
public interface IRenderSettingsProvider
{
    /// <summary>
    /// Reads settings. Implementations must never throw: a broken settings store
    /// should yield <see cref="RenderSettings.Default"/>, not a dead preview pane.
    /// </summary>
    RenderSettings GetSettings();
}
