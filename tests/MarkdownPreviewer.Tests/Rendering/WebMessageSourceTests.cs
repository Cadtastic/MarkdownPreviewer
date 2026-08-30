using MarkdownPreviewer.Rendering.WebView;
using Xunit;

namespace MarkdownPreviewer.Tests.Rendering;

/// <summary>
/// The gate every page-to-host message passes through. The regression that
/// motivates these tests: the page's URI gains a fragment when the reader
/// follows an in-page anchor (a contents-rail entry, a heading link), and an
/// exact-match comparison then rejected everything the page said for the rest
/// of its life — trust clicks were ignored and every render timed out. It
/// looked folder-dependent; it was actually anchor-dependent.
/// </summary>
public sealed class WebMessageSourceTests
{
    [Theory]
    [InlineData("https://assets.mdpreview.invalid/index.html")]
    [InlineData("HTTPS://ASSETS.MDPREVIEW.INVALID/index.html")]
    [InlineData("https://assets.mdpreview.invalid/index.html#-architecture")]
    [InlineData("https://assets.mdpreview.invalid/index.html#getting-started")]
    [InlineData("https://assets.mdpreview.invalid/index.html#")]
    public void MessagesFromTheRenderPageAreAccepted(string source)
    {
        Assert.True(WebView2PreviewSurface.IsFromPreviewPage(source));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a uri")]
    // The document host: same app, but a previewed document must never be able
    // to speak as the page.
    [InlineData("https://doc.mdpreview.invalid/index.html")]
    // A different document on the right host.
    [InlineData("https://assets.mdpreview.invalid/other.html")]
    [InlineData("https://assets.mdpreview.invalid/")]
    // Scheme and query are not forgiven — only the fragment is.
    [InlineData("http://assets.mdpreview.invalid/index.html")]
    [InlineData("https://assets.mdpreview.invalid/index.html?x=1")]
    [InlineData("https://assets.mdpreview.invalid/index.html?x=1#frag")]
    // Lookalike hosts.
    [InlineData("https://assets.mdpreview.invalid.evil.example/index.html")]
    [InlineData("https://evil.example/index.html#https://assets.mdpreview.invalid/index.html")]
    public void EverythingElseIsRejected(string? source)
    {
        Assert.False(WebView2PreviewSurface.IsFromPreviewPage(source));
    }
}
