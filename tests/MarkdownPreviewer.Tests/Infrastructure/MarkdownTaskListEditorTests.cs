using System.Text;
using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Infrastructure.Documents;
using Xunit;

namespace MarkdownPreviewer.Tests.Infrastructure;

/// <summary>
/// The previewer's only write path. These tests care less about the happy case
/// than about everything the editor must refuse: this code runs against files
/// the user did not ask to have modified, so a wrong write is worse than no
/// write.
/// </summary>
public sealed class MarkdownTaskListEditorTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    private static readonly MarkdownTaskListEditor Editor = new(NullLog.Instance);

    private string WriteFile(string content, Encoding? encoding = null)
    {
        string path = Path.Combine(_directory, "tasks.md");
        File.WriteAllBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(content));
        return path;
    }

    private static string ReadRaw(string path) => File.ReadAllText(path, Encoding.UTF8);

    [Fact]
    public void Toggle_ChecksTheMarkerOnTheNamedLine()
    {
        string path = WriteFile("# Todo\n\n- [ ] first\n- [ ] second\n");

        Assert.True(Editor.TryToggle(path, 2, isChecked: true, out _));

        Assert.Equal("# Todo\n\n- [x] first\n- [ ] second\n", ReadRaw(path));
    }

    [Fact]
    public void Toggle_UnchecksToASpaceNotAnEmptyBox()
    {
        string path = WriteFile("- [x] done\n");

        Assert.True(Editor.TryToggle(path, 0, isChecked: false, out _));

        Assert.Equal("- [ ] done\n", ReadRaw(path));
    }

    [Fact]
    public void Toggle_ChangesNothingElseInTheFile()
    {
        // Deliberately awkward neighbours: a fenced block containing text that
        // looks exactly like a task, and trailing whitespace that must survive.
        const string source =
            "# Notes  \n\n```md\n- [ ] not a real task\n```\n\n- [ ] real\t\n\nTail without newline";
        string path = WriteFile(source);

        Assert.True(Editor.TryToggle(path, 6, isChecked: true, out _));

        Assert.Equal(source.Replace("- [ ] real", "- [x] real", StringComparison.Ordinal), ReadRaw(path));
    }

    [Theory]
    [InlineData("- [ ] bullet", "- [x] bullet")]
    [InlineData("* [ ] star", "* [x] star")]
    [InlineData("+ [ ] plus", "+ [x] plus")]
    [InlineData("1. [ ] ordered", "1. [x] ordered")]
    [InlineData("   - [ ] indented", "   - [x] indented")]
    [InlineData("> - [ ] quoted", "> - [x] quoted")]
    public void Toggle_HandlesEveryMarkerShapeGfmAccepts(string line, string expected)
    {
        string path = WriteFile(line + "\n");

        Assert.True(Editor.TryToggle(path, 0, isChecked: true, out _));

        Assert.Equal(expected + "\n", ReadRaw(path));
    }

    [Fact]
    public void Toggle_PreservesCrlfLineEndings()
    {
        string path = WriteFile("- [ ] one\r\n- [ ] two\r\n");

        Assert.True(Editor.TryToggle(path, 1, isChecked: true, out _));

        Assert.Equal("- [ ] one\r\n- [x] two\r\n", ReadRaw(path));
    }

    [Fact]
    public void Toggle_PreservesEncodingAndByteOrderMark()
    {
        string path = Path.Combine(_directory, "utf16.md");
        File.WriteAllText(path, "- [ ] wide\n", new UnicodeEncoding(false, byteOrderMark: true));

        Assert.True(Editor.TryToggle(path, 0, isChecked: true, out _));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        Assert.Equal("- [x] wide\n", File.ReadAllText(path, Encoding.Unicode));
    }

    [Fact]
    public void Toggle_PreservesAUtf8ByteOrderMark()
    {
        string path = WriteFile("﻿- [ ] bommed\n");

        Assert.True(Editor.TryToggle(path, 0, isChecked: true, out _));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    [Fact]
    public void Toggle_ReturnsTheUpdatedTextForTheCaller()
    {
        string path = WriteFile("- [ ] one\r\n- [ ] two\r\n");

        Assert.True(Editor.TryToggle(path, 0, isChecked: true, out string updated));

        // Normalised the way the render pipeline normalises, so the caller can
        // hand it straight back to the page.
        Assert.Equal("- [x] one\n- [ ] two\n", updated);
    }

    [Fact]
    public void Toggle_AgreesWithTheLineNumbersThePageSends()
    {
        // The other half of this contract lives in tests/web/render.test.mjs.
        // Rendering exactly this document and clicking the first checkbox makes
        // the page post line 10 — front matter it never parsed (3 lines) plus
        // the item's own offset in the body (7). The decoy inside the fence is
        // not a checkbox, so it must not shift anything. If either side's line
        // arithmetic drifts, one of the two suites fails.
        const string source =
            "---\ntitle: Demo\n---\n\n# Tasks\n\n```md\n- [ ] decoy in a fence\n```\n\n- [ ] alpha\n- [x] beta\n";
        string path = WriteFile(source);

        Assert.True(Editor.TryToggle(path, 10, isChecked: true, out _));

        Assert.Equal(
            source.Replace("- [ ] alpha", "- [x] alpha", StringComparison.Ordinal),
            ReadRaw(path));
    }

    [Fact]
    public void Toggle_RefusesWhenTheLineIsNotATask()
    {
        const string source = "# Heading\n\n- [ ] task\n";
        string path = WriteFile(source);

        Assert.False(Editor.TryToggle(path, 0, isChecked: true, out _));

        Assert.Equal(source, ReadRaw(path));
    }

    [Fact]
    public void Toggle_RefusesWhenTheFileAlreadyDisagrees()
    {
        // The file changed under the preview: the page thinks it is unchecking
        // a box that is already clear. Writing would be guesswork.
        const string source = "- [ ] already clear\n";
        string path = WriteFile(source);

        Assert.False(Editor.TryToggle(path, 0, isChecked: false, out _));

        Assert.Equal(source, ReadRaw(path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Toggle_RefusesLinesThatDoNotExist(int lineIndex)
    {
        const string source = "- [ ] one\n- [ ] two\n";
        string path = WriteFile(source);

        Assert.False(Editor.TryToggle(path, lineIndex, isChecked: true, out _));

        Assert.Equal(source, ReadRaw(path));
    }

    [Fact]
    public void Toggle_RefusesFilesWhoseBytesWouldNotSurviveTheRoundTrip()
    {
        // A stray 0x80 is not valid UTF-8. Decoding turns it into U+FFFD, and
        // re-encoding would write those replacement bytes over the user's
        // content — so the whole file is refused rather than quietly mangled.
        string path = Path.Combine(_directory, "invalid.md");
        File.WriteAllBytes(path, [.. "- [ ] task\n"u8.ToArray(), 0x80]);
        byte[] before = File.ReadAllBytes(path);

        Assert.False(Editor.TryToggle(path, 0, isChecked: true, out _));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Toggle_RefusesAMissingFileWithoutThrowing()
    {
        Assert.False(Editor.TryToggle(
            Path.Combine(_directory, "gone.md"), 0, isChecked: true, out _));
    }

    [Fact]
    public void Toggle_RefusesAReadOnlyFileAndLeavesItIntact()
    {
        const string source = "- [ ] locked\n";
        string path = WriteFile(source);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            Assert.False(Editor.TryToggle(path, 0, isChecked: true, out _));
            Assert.Equal(source, ReadRaw(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Toggle_LeavesNoTemporaryFilesBehind()
    {
        string path = WriteFile("- [ ] one\n");

        Assert.True(Editor.TryToggle(path, 0, isChecked: true, out _));

        Assert.Equal(["tasks.md"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception) { /* best effort */ }
    }

    private sealed class NullLog : IDiagnosticLog
    {
        public static NullLog Instance { get; } = new();

        public bool IsEnabled(DiagnosticLevel level) => false;

        public void Write(DiagnosticLevel level, string message, Exception? exception = null)
        {
        }
    }
}
