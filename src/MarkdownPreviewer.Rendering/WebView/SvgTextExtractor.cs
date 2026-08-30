using System.Collections.Concurrent;
using System.Text;
using System.Xml;

namespace MarkdownPreviewer.Rendering.WebView;

/// <summary>
/// Extracts the visible text from an SVG file, so the search can match what a
/// diagram embedded through <c>&lt;img&gt;</c> draws.
/// </summary>
/// <remarks>
/// <para>An SVG loaded via <c>&lt;img&gt;</c> is a separate, non-scriptable
/// document — the page cannot reach its DOM, but its <c>&lt;text&gt;</c>
/// content is right there in the file the host is already serving. This reads
/// it once per file version and hands it to the page for matching.</para>
///
/// <para>The file is untrusted input, so the reader is locked down: DTDs are
/// prohibited outright (no entity expansion, no external resolution), and only
/// character data outside non-visible containers (<c>style</c>, <c>script</c>,
/// <c>defs</c>, <c>metadata</c>, <c>title</c>, <c>desc</c>) is collected — the
/// same exclusions the in-page walker applies to inline diagrams, and for the
/// same reason: counting a stylesheet's selectors as "text" once made a search
/// report 146 matches on a document visibly containing two.</para>
/// </remarks>
internal static class SvgTextExtractor
{
    /// <summary>Files larger than this are skipped; diagrams are small.</summary>
    private const long MaximumBytes = 2 * 1024 * 1024;

    /// <summary>Cap on extracted text per file — plenty for matching.</summary>
    private const int MaximumTextLength = 8 * 1024;

    private static readonly ConcurrentDictionary<string, (DateTime WriteTimeUtc, string Text)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SkippedContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "script", "defs", "metadata", "title", "desc",
    };

    /// <summary>
    /// Returns the SVG's visible text, or an empty string when the file is
    /// missing, oversized, or not well-formed XML. Never throws.
    /// </summary>
    public static string Extract(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > MaximumBytes)
            {
                return string.Empty;
            }

            if (Cache.TryGetValue(fullPath, out (DateTime WriteTimeUtc, string Text) cached) &&
                cached.WriteTimeUtc == info.LastWriteTimeUtc)
            {
                return cached.Text;
            }

            string text = ReadVisibleText(fullPath);
            Cache[fullPath] = (info.LastWriteTimeUtc, text);
            return text;
        }
        catch (Exception)
        {
            // Malformed XML, IO races, encoding surprises: an unsearchable
            // image, never a failed preview.
            return string.Empty;
        }
    }

    private static string ReadVisibleText(string fullPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = 8 * 1024 * 1024,
        };

        var builder = new StringBuilder();
        int skipDepth = 0;

        using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = XmlReader.Create(stream, settings);

        while (reader.Read() && builder.Length < MaximumTextLength)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when SkippedContainers.Contains(reader.LocalName):
                    if (!reader.IsEmptyElement)
                    {
                        skipDepth++;
                    }

                    break;

                case XmlNodeType.EndElement when skipDepth > 0 && SkippedContainers.Contains(reader.LocalName):
                    skipDepth--;
                    break;

                case XmlNodeType.Text or XmlNodeType.CDATA when skipDepth == 0:
                    string value = reader.Value.Trim();
                    if (value.Length > 0)
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append(' ');
                        }

                        builder.Append(value);
                    }

                    break;
            }
        }

        return builder.Length <= MaximumTextLength
            ? builder.ToString()
            : builder.ToString(0, MaximumTextLength);
    }
}
