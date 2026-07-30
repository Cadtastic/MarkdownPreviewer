using MarkdownPreviewer.Domain.Documents;

namespace MarkdownPreviewer.Domain.Rendering;

/// <summary>
/// Everything the render surface needs for one document, resolved and validated.
/// </summary>
public sealed class RenderRequest
{
    public RenderRequest(MarkdownDocument document, AppearanceTheme theme, RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);

        Document = document;
        Theme = theme;
        Settings = settings.Normalised();
    }

    public MarkdownDocument Document { get; }

    public AppearanceTheme Theme { get; }

    public RenderSettings Settings { get; }
}
