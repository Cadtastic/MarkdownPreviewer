namespace MarkdownPreviewer.Domain.Documents;

/// <summary>
/// A Markdown document that has been read into memory and is ready to render.
/// </summary>
public sealed class MarkdownDocument
{
    /// <summary>
    /// Hard ceiling on document size. A preview pane is not a text editor; past
    /// this point the render cost is no longer worth paying on a mere selection
    /// change, and the document is truncated with a visible notice instead.
    /// </summary>
    public const int DefaultMaximumBytes = 4 * 1024 * 1024;

    public MarkdownDocument(string source, DocumentLocation location, bool wasTruncated, long originalByteCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(location);

        if (originalByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalByteCount), originalByteCount,
                "Byte count cannot be negative.");
        }

        Source = source;
        Location = location;
        WasTruncated = wasTruncated;
        OriginalByteCount = originalByteCount;
    }

    /// <summary>The Markdown text, decoded to UTF-16.</summary>
    public string Source { get; }

    /// <summary>Where the document came from.</summary>
    public DocumentLocation Location { get; }

    /// <summary>True when <see cref="Source"/> holds only the leading portion of the file.</summary>
    public bool WasTruncated { get; }

    /// <summary>Size of the file on disk, before decoding or truncation.</summary>
    public long OriginalByteCount { get; }

    /// <summary>True when there is nothing to render.</summary>
    public bool IsEmpty => Source.Length == 0;
}
