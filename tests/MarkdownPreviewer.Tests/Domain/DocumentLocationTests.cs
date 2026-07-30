using MarkdownPreviewer.Domain.Documents;
using Xunit;

namespace MarkdownPreviewer.Tests.Domain;

public sealed class DocumentLocationTests
{
    [Fact]
    public void FromFilePath_ExposesDirectoryAndFileName()
    {
        DocumentLocation location = DocumentLocation.FromFilePath(@"C:\docs\notes\readme.md");

        Assert.True(location.HasDirectory);
        Assert.Equal(@"C:\docs\notes", location.DirectoryPath);
        Assert.Equal("readme.md", location.FileName);
    }

    [Fact]
    public void Unknown_HasNoDirectory()
    {
        // This is the case that decides whether relative images can resolve, so
        // it needs to be unambiguous rather than an empty string.
        Assert.False(DocumentLocation.Unknown.HasDirectory);
        Assert.Null(DocumentLocation.Unknown.DirectoryPath);
        Assert.Equal(string.Empty, DocumentLocation.Unknown.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromFilePath_RejectsEmptyPaths(string path) =>
        Assert.Throws<ArgumentException>(() => DocumentLocation.FromFilePath(path));

    [Fact]
    public void Equality_IsCaseInsensitive()
    {
        DocumentLocation left = DocumentLocation.FromFilePath(@"C:\Docs\README.md");
        DocumentLocation right = DocumentLocation.FromFilePath(@"c:\docs\readme.md");

        // Windows paths are case-insensitive; treating them otherwise would make
        // the render surface remap its virtual host on every selection.
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}
