namespace MarkdownPreviewer.Domain.Documents;

/// <summary>
/// Where a previewed document lives on disk.
/// </summary>
/// <remarks>
/// The preview pane can also hand us a document with no path at all — a stream
/// pulled out of a zip, a search-index item, an attachment. Those are represented
/// by <see cref="Unknown"/>, which reports <see cref="HasDirectory"/> as false.
/// Callers must treat "no directory" as a first-class case, because it decides
/// whether relative image references can be resolved at all.
/// </remarks>
public sealed class DocumentLocation : IEquatable<DocumentLocation>
{
    /// <summary>A document with no on-disk location (stream-initialised).</summary>
    public static readonly DocumentLocation Unknown = new(null, null);

    private DocumentLocation(string? fullPath, string? directoryPath)
    {
        FullPath = fullPath;
        DirectoryPath = directoryPath;
    }

    /// <summary>Full path to the file, or <see langword="null"/> when unknown.</summary>
    public string? FullPath { get; }

    /// <summary>Directory containing the file, or <see langword="null"/> when unknown.</summary>
    public string? DirectoryPath { get; }

    /// <summary>File name including extension, or an empty string when unknown.</summary>
    public string FileName =>
        FullPath is null ? string.Empty : Path.GetFileName(FullPath);

    /// <summary>True when relative references in the document can be resolved.</summary>
    public bool HasDirectory => DirectoryPath is not null;

    /// <summary>
    /// Creates a location from a file path supplied by the shell.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The path is empty, malformed, or not rooted. The shell should never hand
    /// us such a path; if it does, that is a bug worth surfacing rather than
    /// papering over.
    /// </exception>
    public static DocumentLocation FromFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A document path must not be empty.", nameof(filePath));
        }

        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"'{filePath}' is not a usable file path.", nameof(filePath), ex);
        }

        string? directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException($"'{filePath}' has no containing directory.", nameof(filePath));
        }

        return new DocumentLocation(full, directory);
    }

    /// <summary>
    /// Creates a location that knows the document's name but not where it lives.
    /// </summary>
    /// <remarks>
    /// Used when a stream-initialised document reports a name through
    /// <c>IStream.Stat</c> — enough to identify it in logs and messages, not
    /// enough to resolve relative references: <see cref="HasDirectory"/> stays
    /// false. Blank input yields <see cref="Unknown"/>.
    /// </remarks>
    public static DocumentLocation FromDisplayName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Unknown : new DocumentLocation(name.Trim(), null);

    public bool Equals(DocumentLocation? other) =>
        other is not null &&
        string.Equals(FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as DocumentLocation);

    public override int GetHashCode() =>
        FullPath is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(FullPath);

    public override string ToString() => FullPath ?? "<stream>";
}
