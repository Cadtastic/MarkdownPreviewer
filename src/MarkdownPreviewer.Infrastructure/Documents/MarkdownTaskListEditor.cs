using System.Text;
using System.Text.RegularExpressions;
using MarkdownPreviewer.Application.Abstractions;

namespace MarkdownPreviewer.Infrastructure.Documents;

/// <summary>
/// Flips a task-list marker in place: one character changes, every other byte
/// of the file is proven untouched before anything is written.
/// </summary>
/// <remarks>
/// <para><b>The safety argument.</b> This is the previewer's only write path,
/// and it must never be able to damage a document. The file is re-read from
/// disk on every toggle (never reconstructed from the rendered page), decoded
/// with its own encoding, and re-encoded unchanged for comparison: unless the
/// round trip reproduces the original payload byte for byte, the file is
/// refused as un-editable rather than risked. Only then is the single marker
/// character replaced and the whole thing written back — same encoding, same
/// BOM, same line endings, through a temp file and an atomic replace.</para>
///
/// <para><b>Line numbers.</b> The page counts lines over text whose line
/// endings were normalised for rendering; CRLF, LF and lone CR each count as
/// exactly one terminator there, so this class scans the un-normalised text
/// with the same three terminators and the indices agree.</para>
///
/// <para><b>Verification.</b> The page also says which state it believes the
/// box is currently in (by sending the new one). If the marker on that line is
/// not a task marker, or is not in the expected state — the file changed under
/// the preview, or the mapping is stale — nothing is written and the caller is
/// told, so it can re-render from disk and resynchronise.</para>
/// </remarks>
public sealed partial class MarkdownTaskListEditor(IDiagnosticLog log) : ITaskListEditor
{
    /// <summary>
    /// A GFM task marker at the start of a list item: optional blockquote
    /// nesting and indentation, a bullet or ordered marker, then <c>[ ]</c> /
    /// <c>[x]</c>. The state character's position is what gets edited.
    /// </summary>
    [GeneratedRegex(@"^[ \t>]*(?:[-*+]|\d{1,9}[.)])[ \t]+\[(?<state>[ xX])\]", RegexOptions.ExplicitCapture)]
    private static partial Regex TaskMarker();

    public bool TryToggle(string filePath, int lineIndex, bool isChecked, out string updatedText)
    {
        updatedText = string.Empty;

        try
        {
            byte[] original = File.ReadAllBytes(filePath);
            string text = MarkdownTextDecoder.DecodeForEditing(
                original, out Encoding encoding, out int preambleLength);

            // Byte fidelity first: if decode/encode does not reproduce the file
            // exactly (invalid byte sequences become replacement characters),
            // editing would silently rewrite content far from the checkbox.
            if (!encoding.GetBytes(text).AsSpan().SequenceEqual(original.AsSpan(preambleLength)))
            {
                log.Warn($"Refusing to edit '{filePath}': its bytes do not survive a decode/encode round trip.");
                return false;
            }

            if (!TryFindLine(text, lineIndex, out int lineStart, out int lineLength))
            {
                log.Warn($"Refusing to edit '{filePath}': it has no line {lineIndex}.");
                return false;
            }

            Match marker = TaskMarker().Match(text, lineStart, lineLength);
            if (!marker.Success)
            {
                log.Warn($"Refusing to edit '{filePath}': line {lineIndex} is not a task item.");
                return false;
            }

            Group state = marker.Groups["state"];
            bool currentlyChecked = state.ValueSpan[0] != ' ';
            if (currentlyChecked == isChecked)
            {
                // The page believes the opposite of what the file says — the
                // file changed underneath the preview. Do not touch it.
                log.Warn($"Refusing to edit '{filePath}': line {lineIndex} is already {(isChecked ? "checked" : "unchecked")}.");
                return false;
            }

            string newText = string.Concat(
                text.AsSpan(0, state.Index),
                isChecked ? "x" : " ",
                text.AsSpan(state.Index + 1));

            byte[] updated = new byte[preambleLength + encoding.GetByteCount(newText)];
            original.AsSpan(0, preambleLength).CopyTo(updated);
            encoding.GetBytes(newText, updated.AsSpan(preambleLength));

            WriteAtomically(filePath, updated);

            // What the render pipeline would produce from the new bytes, so the
            // caller's in-memory document stays in step without another read.
            updatedText = MarkdownTextDecoder.Decode(updated);
            log.Info($"Task on line {lineIndex} of '{filePath}' set to {(isChecked ? "checked" : "unchecked")}.");
            return true;
        }
        catch (Exception ex)
        {
            // Locked, read-only, deleted between render and click — all the
            // same outcome: the file is untouched and the caller resyncs.
            log.Warn($"Could not update the task list in '{filePath}'.", ex);
            return false;
        }
    }

    /// <summary>
    /// Locates line <paramref name="lineIndex"/> (0-based) in text that still
    /// has its original line endings. CRLF, LF and lone CR each terminate a
    /// line — the same three the render-side normalisation collapses — so these
    /// indices agree with the page's. The terminator is not included.
    /// </summary>
    private static bool TryFindLine(string text, int lineIndex, out int start, out int length)
    {
        start = 0;
        length = 0;

        if (lineIndex < 0)
        {
            return false;
        }

        int line = 0;
        int position = 0;

        while (line < lineIndex)
        {
            int next = text.AsSpan(position).IndexOfAny('\r', '\n');
            if (next < 0)
            {
                return false;   // ran out of lines before reaching the target
            }

            position += next + 1;
            if (text[position - 1] == '\r' && position < text.Length && text[position] == '\n')
            {
                position++;     // CRLF is one terminator, not two
            }

            line++;
        }

        start = position;
        int end = text.AsSpan(position).IndexOfAny('\r', '\n');
        length = end < 0 ? text.Length - position : end;
        return true;
    }

    /// <summary>
    /// Writes via a sibling temp file and an atomic replace, so a crash
    /// mid-write can never leave the document half-written.
    /// </summary>
    private static void WriteAtomically(string filePath, byte[] content)
    {
        string directory = Path.GetDirectoryName(filePath)
            ?? throw new IOException($"'{filePath}' has no containing directory.");

        string temp = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(temp, content);
            File.Replace(temp, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception) { /* the temp file is orphaned; nothing else to do */ }
            throw;
        }
    }
}
