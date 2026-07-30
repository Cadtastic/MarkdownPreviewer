using MarkdownPreviewer.Domain.Documents;

namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>
/// Turns whatever the shell initialised us with into a <see cref="MarkdownDocument"/>.
/// </summary>
/// <remarks>
/// There is one implementation per shell initialisation interface
/// (<c>IInitializeWithFile</c>, <c>IInitializeWithStream</c>). The use case does
/// not care which one it got — only whether the resulting document knows its
/// directory, which determines whether relative images can resolve.
/// </remarks>
public interface IMarkdownSourceReader
{
    /// <summary>True when this reader currently has a source to read.</summary>
    bool HasSource { get; }

    /// <summary>
    /// Reads and decodes the source.
    /// </summary>
    /// <param name="maximumBytes">Read at most this many bytes; truncate beyond it.</param>
    /// <exception cref="InvalidOperationException"><see cref="HasSource"/> is false.</exception>
    /// <exception cref="IOException">The source could not be read.</exception>
    Task<MarkdownDocument> ReadAsync(int maximumBytes, CancellationToken cancellationToken);
}
