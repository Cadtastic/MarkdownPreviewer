namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>
/// Shows a linked file where it lives: File Explorer is navigated to the
/// file's folder and the file is selected, which puts it in the preview pane.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="IExternalLinkLauncher"/> for local links.
/// Launching hands the file to another program; revealing keeps the reader in
/// Explorer and merely moves the selection, which is why revealing carries no
/// file-type restrictions — selecting a file executes nothing.
/// </remarks>
public interface IDocumentRevealer
{
    /// <summary>
    /// Navigates the Explorer window hosting the preview to
    /// <paramref name="fullPath"/> and selects it. A directory is navigated
    /// into rather than selected.
    /// </summary>
    /// <param name="fullPath">The existing file or directory to reveal.</param>
    /// <param name="hostWindow">
    /// A window inside the preview pane, used to find which Explorer window is
    /// hosting it.
    /// </param>
    /// <param name="previewedDirectory">
    /// The folder of the document currently on screen — the folder the hosting
    /// Explorer tab is showing, which is what distinguishes that tab from the
    /// window's other tabs.
    /// </param>
    /// <remarks>
    /// Fire-and-forget by design: revealing changes Explorer's selection, and a
    /// selection change tears down the preview handler that called this. The
    /// work must not depend on the caller staying alive.
    /// </remarks>
    void Reveal(string fullPath, nint hostWindow, string? previewedDirectory);
}
