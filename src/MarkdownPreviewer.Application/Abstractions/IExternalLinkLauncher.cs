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

    /// <summary>
    /// Opens a local file with its default application.
    /// </summary>
    /// <remarks>
    /// For links between documents ("see <c>docs/architecture.md</c>"). The path
    /// arrives pre-resolved and confined by the caller; implementations still
    /// re-validate — existence and a document-type allowlist — because launching
    /// a file is the most dangerous thing a preview can be talked into.
    /// </remarks>
    void LaunchDocument(string fullPath);
}
