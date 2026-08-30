using MarkdownPreviewer.Infrastructure.Shell;
using Xunit;

namespace MarkdownPreviewer.Tests.Infrastructure;

/// <summary>
/// The revealer's pure pieces. The tab-matching walk and the navigation itself
/// need a live Explorer, but the two predicates they stand on — turning a
/// ShellWindows LocationURL into a path, and deciding whether two paths mean
/// the same folder — decide which tab gets navigated, so they are worth pinning.
/// </summary>
public sealed class ShellWindowsDocumentRevealerTests
{
    [Theory]
    [InlineData("file:///C:/Users/Someone/Documents", @"C:\Users\Someone\Documents")]
    [InlineData("file:///C:/Users/Someone/My%20Docs", @"C:\Users\Someone\My Docs")]
    [InlineData("file:///C:/", @"C:\")]
    [InlineData("file://server/share/folder", @"\\server\share\folder")]
    public void LocationUrls_TranslateToLocalPaths(string locationUrl, string expected)
    {
        Assert.Equal(expected, ShellWindowsDocumentRevealer.TryGetLocalPath(locationUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // Virtual views a preview can be hosted from but never navigated as paths.
    [InlineData("search-ms:displayname=Search%20Results&crumb=location:C%3A%5C")]
    [InlineData("shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}")]
    [InlineData("not a url")]
    public void NonFileLocations_TranslateToNothing(string? locationUrl)
    {
        Assert.Null(ShellWindowsDocumentRevealer.TryGetLocalPath(locationUrl));
    }

    [Theory]
    [InlineData(@"C:\Docs", @"C:\Docs")]
    [InlineData(@"C:\Docs", @"c:\docs")]
    [InlineData(@"C:\Docs\", @"C:\Docs")]
    [InlineData(@"C:\Docs\sub\..", @"C:\Docs")]
    public void PathsEqual_ToleratesCaseSlashesAndDots(string left, string right)
    {
        Assert.True(ShellWindowsDocumentRevealer.PathsEqual(left, right));
    }

    [Theory]
    [InlineData(@"C:\Docs", @"C:\Docs\sub")]
    [InlineData(@"C:\Docs", @"D:\Docs")]
    [InlineData(@"C:\Docs", null)]
    [InlineData(null, null)]
    [InlineData(@"C:\Docs", "")]
    public void PathsEqual_RejectsDifferentPlaces(string? left, string? right)
    {
        Assert.False(ShellWindowsDocumentRevealer.PathsEqual(left, right));
    }
}
