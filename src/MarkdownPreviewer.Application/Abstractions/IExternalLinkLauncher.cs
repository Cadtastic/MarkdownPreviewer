namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>
/// Opens a link the user clicked inside the preview.
/// </summary>
/// <remarks>
/// Separated from the render surface because this is the one operation in the
/// whole extension that leaves our process boundary on behalf of untrusted
/// content. Implementations are expected to re-validate the scheme rather than
/// trusting the caller.
/// </remarks>
public interface IExternalLinkLauncher
{
    void Launch(string url);
}
