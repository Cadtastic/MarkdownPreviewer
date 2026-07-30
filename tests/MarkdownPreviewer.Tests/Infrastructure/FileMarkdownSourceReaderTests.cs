using System.Text;
using MarkdownPreviewer.Domain.Documents;
using MarkdownPreviewer.Infrastructure.Documents;
using Xunit;

namespace MarkdownPreviewer.Tests.Infrastructure;

public sealed class FileMarkdownSourceReaderTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void HasSource_IsFalseUntilAFileIsSet()
    {
        var reader = new FileMarkdownSourceReader();

        Assert.False(reader.HasSource);
    }

    [Fact]
    public async Task ReadAsync_ReportsTheContainingDirectory()
    {
        string path = WriteFile("doc.md", "# Hello");
        var reader = new FileMarkdownSourceReader();
        reader.SetFile(path);

        MarkdownDocument document = await reader.ReadAsync(1024 * 1024, CancellationToken.None);

        Assert.True(document.Location.HasDirectory);
        Assert.Equal(_directory, document.Location.DirectoryPath);
        Assert.False(document.WasTruncated);
    }

    [Fact]
    public async Task ReadAsync_TruncatesAtTheCapAndSaysSo()
    {
        // 200 lines of 100 characters, read with a 4 KB cap.
        string content = string.Join('\n', Enumerable.Range(0, 200).Select(i => new string('x', 99)));
        string path = WriteFile("big.md", content);

        var reader = new FileMarkdownSourceReader();
        reader.SetFile(path);

        MarkdownDocument document = await reader.ReadAsync(4096, CancellationToken.None);

        Assert.True(document.WasTruncated);
        Assert.True(document.Source.Length <= 4096);
        Assert.True(document.OriginalByteCount > 4096);

        // Truncation must land on a line boundary, otherwise the tail of the
        // document can end mid-fence and corrupt the parse.
        Assert.EndsWith("\n", document.Source);
    }

    [Fact]
    public async Task ReadAsync_SucceedsWhileTheFileIsOpenForWritingElsewhere()
    {
        // The single most annoying possible failure would be refusing to preview a
        // file because the user has it open in an editor.
        string path = WriteFile("locked.md", "# Open in an editor");

        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var reader = new FileMarkdownSourceReader();
        reader.SetFile(path);

        MarkdownDocument document = await reader.ReadAsync(1024 * 1024, CancellationToken.None);

        Assert.Equal("# Open in an editor", document.Source);
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenNoFileWasSet()
    {
        var reader = new FileMarkdownSourceReader();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(1024, CancellationToken.None));
    }

    [Fact]
    public void SetFile_RejectsAnInvalidPathEagerly()
    {
        var reader = new FileMarkdownSourceReader();

        // Fails at Initialize, where the shell can report it, rather than at
        // DoPreview, where it looks like a render bug.
        Assert.Throws<ArgumentException>(() => reader.SetFile(string.Empty));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort in a temp directory.
        }
    }
}
