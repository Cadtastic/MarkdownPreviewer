using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Domain.Documents;

namespace MarkdownPreviewer.Infrastructure.Documents;

/// <summary>
/// Reads the document from a COM <see cref="IStream"/>, as supplied through
/// <c>IInitializeWithStream</c>.
/// </summary>
/// <remarks>
/// Used when the host has no file path to give us — a document inside a
/// compressed folder, a Search result, an Outlook attachment. Relative image
/// references cannot be resolved in this mode; the document location is
/// <see cref="DocumentLocation.Unknown"/> and the render surface degrades
/// gracefully.
/// </remarks>
public sealed class StreamMarkdownSourceReader : IMarkdownSourceReader
{
    private IStream? _stream;

    public bool HasSource => _stream is not null;

    /// <summary>Called from <c>IInitializeWithStream.Initialize</c>.</summary>
    public void SetStream(IStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Reset();
        _stream = stream;
    }

    public void Reset()
    {
        if (_stream is null)
        {
            return;
        }

        // The shell handed us a reference; we own releasing it.
        try
        {
            Marshal.ReleaseComObject(_stream);
        }
        catch (ArgumentException)
        {
            // Not an RCW (a managed test double). Nothing to release.
        }
        finally
        {
            _stream = null;
        }
    }

    public Task<MarkdownDocument> ReadAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("No stream has been set on this reader.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // IStream is apartment-affine and synchronous; copying it into memory on
        // the calling (STA) thread is both correct and, for preview-sized
        // documents, fast enough that async buys nothing.
        using var memory = new MemoryStream();
        CopyTo(_stream, memory, maximumBytes + 1, cancellationToken);

        byte[] bytes = memory.GetBuffer();
        int length = (int)memory.Length;
        bool truncated = length > maximumBytes;
        if (truncated)
        {
            length = maximumBytes;
        }

        string text = MarkdownTextDecoder.Decode(bytes.AsSpan(0, length));
        var document = new MarkdownDocument(text, DocumentLocation.Unknown, truncated, memory.Length);
        return Task.FromResult(document);
    }

    private static void CopyTo(IStream source, Stream destination, int maximumBytes, CancellationToken cancellationToken)
    {
        const int ChunkSize = 64 * 1024;
        byte[] chunk = new byte[ChunkSize];

        // IStream.Read reports the byte count through a native int*, so we need a
        // pinned cell to hand it.
        IntPtr readCell = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            long remaining = maximumBytes;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int request = (int)Math.Min(ChunkSize, remaining);
                source.Read(chunk, request, readCell);
                int read = Marshal.ReadInt32(readCell);

                if (read <= 0)
                {
                    break;
                }

                destination.Write(chunk, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(readCell);
        }
    }
}
