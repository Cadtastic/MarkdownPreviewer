using System.Text;
using MarkdownPreviewer.Domain.Documents;
using MarkdownPreviewer.Infrastructure.Documents;
using Xunit;

namespace MarkdownPreviewer.Tests.Infrastructure;

/// <summary>
/// Encoding detection tests. These exist because getting it wrong does not throw
/// — it renders mojibake, or a document that looks like one long line of spaced
/// characters, and the cause is not obvious from the symptom.
/// </summary>
public sealed class MarkdownTextDecoderTests
{
    // MarkdownTextDecoder is internal, so drive it through the public reader.
    private static string Decode(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".md");
        File.WriteAllBytes(path, bytes);
        try
        {
            var reader = new FileMarkdownSourceReader();
            reader.SetFile(path);
            MarkdownDocument document = reader.ReadAsync(1024 * 1024, CancellationToken.None).GetAwaiter().GetResult();
            return document.Source;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Utf8WithoutBom_RoundTrips()
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes("# Héllo — wörld ✓\n");

        Assert.Equal("# Héllo — wörld ✓\n", Decode(bytes));
    }

    [Fact]
    public void Utf8WithBom_StripsTheBom()
    {
        byte[] bytes = new UTF8Encoding(true).GetBytes("# Title\n");

        string text = Decode(bytes);

        Assert.Equal("# Title\n", text);
        Assert.DoesNotContain('\uFEFF', text);
    }

    [Fact]
    public void Utf16LittleEndian_IsDetected()
    {
        // PowerShell's redirection operator produced exactly this for years.
        byte[] bytes = new UnicodeEncoding(false, true).GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("# Title\n"))
            .ToArray();

        Assert.Equal("# Title\n", Decode(bytes));
    }

    [Fact]
    public void Utf16BigEndian_IsDetected()
    {
        byte[] bytes = new UnicodeEncoding(true, true).GetPreamble()
            .Concat(Encoding.BigEndianUnicode.GetBytes("# Title\n"))
            .ToArray();

        Assert.Equal("# Title\n", Decode(bytes));
    }

    [Fact]
    public void CrLfAndLoneCr_AreNormalisedToLf()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("a\r\nb\rc\nd");

        // The JS side treats a lone CR as no line break at all, so normalising
        // here rather than in the page keeps the parser's view consistent.
        Assert.Equal("a\nb\nc\nd", Decode(bytes));
    }

    [Fact]
    public void EmptyFile_ProducesAnEmptyDocument() =>
        Assert.Equal(string.Empty, Decode([]));
}
