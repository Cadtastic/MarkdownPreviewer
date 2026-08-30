namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>
/// Flips one GFM task-list checkbox inside a Markdown file on disk.
/// </summary>
/// <remarks>
/// The only write path the previewer has, and deliberately the narrowest one
/// imaginable: a single character, on a single verified line, in a file the
/// user is looking at. The document is never regenerated from the rendered
/// page — the file is re-read, the marker is checked to be in the state the
/// page believes it is in, and exactly one byte-run changes. Anything
/// surprising (the line is not a task, the state does not match, the file's
/// bytes cannot be reproduced faithfully) refuses rather than writes.
/// </remarks>
public interface ITaskListEditor
{
    /// <summary>
    /// Sets the task marker on <paramref name="lineIndex"/> (0-based) of
    /// <paramref name="filePath"/> to <paramref name="isChecked"/>.
    /// </summary>
    /// <param name="updatedText">
    /// On success, the file's new content decoded the same way the preview
    /// reader decodes it (line endings normalised), so the caller can keep its
    /// in-memory document in step without re-reading the file.
    /// </param>
    /// <returns>
    /// True when the file was updated. False when it was not — wrong line,
    /// marker already in the requested state, unwritable file — in which case
    /// the file is untouched.
    /// </returns>
    bool TryToggle(string filePath, int lineIndex, bool isChecked, out string updatedText);
}
