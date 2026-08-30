namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>
/// Remembers, per document, whether the reader turned task-checkbox editing on.
/// </summary>
/// <remarks>
/// Per document rather than per user because the answer genuinely differs by
/// file: a personal checklist is one you want to tick straight from the
/// preview, while a README you happen to be reading is one you would rather not
/// alter with a stray click. Turning editing on for the file in front of you
/// should not arm every other file you look at afterwards.
/// </remarks>
public interface ITaskEditModeStore
{
    /// <summary>
    /// Whether editing is on for <paramref name="fullPath"/>. Off by default,
    /// and always off for an item with no file behind it.
    /// </summary>
    bool IsEnabled(string? fullPath);

    /// <summary>
    /// Records the choice for <paramref name="fullPath"/>. Failure to persist
    /// is swallowed: an unwritable preference must not break the preview.
    /// </summary>
    void SetEnabled(string? fullPath, bool enabled);
}
