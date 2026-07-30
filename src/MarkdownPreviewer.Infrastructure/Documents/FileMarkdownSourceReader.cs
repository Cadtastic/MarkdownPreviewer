using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Domain.Documents;

namespace MarkdownPreviewer.Infrastructure.Documents;

/// <summary>
/// Reads the document from a file path, as supplied through
/// <c>IInitializeWithFile</c>.
/// </summary>
/// <remarks>
/// This is the reader that makes relative images work, because it is the only one
/// that knows the containing directory. Microsoft's guidance prefers stream
/// initialisation for isolation, and we implement that too
/// (<see cref="StreamMarkdownSourceReader"/>) — but a Markdown previewer that
/// cannot show the screenshots sitting next to the document is not much of a
/// Markdown previewer, so file initialisation is registered as the primary path.
/// </remarks>
public sealed class FileMarkdownSourceReader : IMarkdownSourceReader
{
    private string? _filePath;

    public bool HasSource => _filePath is not null;

    /// <summary>Called from <c>IInitializeWithFile.Initialize</c>.</summary>
    public void SetFile(string filePath)
    {
        // Validate eagerly so a bad path fails at Initialize (where the shell can
        // report it) rather than at DoPreview (where it looks like a render bug).
        _ = DocumentLocation.FromFilePath(filePath);
        _filePath = filePath;
    }

    public void Reset() => _filePath = null;

    public async Task<MarkdownDocument> ReadAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        if (_filePath is null)
        {
            throw new InvalidOperationException("No file has been set on this reader.");
        }

        DocumentLocation location = DocumentLocation.FromFilePath(_filePath);

        // FileShare.ReadWrite | Delete: the user very likely has this file open in
        // an editor. Refusing to preview a file because VS Code has it open would
        // be the single most annoying possible failure mode.
        await using var stream = new FileStream(
            _filePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 64 * 1024,
            });

        long length = 0;
        try
        {
            length = stream.Length;
        }
        catch (IOException)
        {
            // Non-seekable (a reparse point or a shell namespace bridge). Fall
            // back to streaming and reporting the count we actually read.
        }

        (byte[] buffer, int read, bool truncated) =
            await ReadCappedAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);

        string text = MarkdownTextDecoder.Decode(buffer.AsSpan(0, read));
        return new MarkdownDocument(text, location, truncated, length > 0 ? length : read);
    }

    /// <summary>
    /// Reads up to <paramref name="maximumBytes"/>, plus one extra byte so we can
    /// distinguish "exactly at the cap" from "there is more".
    /// </summary>
    internal static async Task<(byte[] Buffer, int Read, bool Truncated)> ReadCappedAsync(
        Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        int cap = Math.Max(maximumBytes, MarkdownTextDecoder.MaximumPreambleLength);
        byte[] buffer = new byte[cap + 1];
        int total = 0;

        while (total < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > cap)
        {
            // Do not slice mid-surrogate or mid-UTF8-sequence; back off to the
            // last newline so the truncated document still parses cleanly.
            int safeEnd = LastNewlineBefore(buffer, cap);
            return (buffer, safeEnd, true);
        }

        return (buffer, total, false);
    }

    private static int LastNewlineBefore(byte[] buffer, int limit)
    {
        for (int i = limit - 1; i >= 0 && i > limit - 4096; i--)
        {
            if (buffer[i] == (byte)'\n')
            {
                return i + 1;
            }
        }

        return limit;
    }
}
