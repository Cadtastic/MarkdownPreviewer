using MarkdownPreviewer.Rendering.WebView;
using Xunit;

namespace MarkdownPreviewer.Tests.Rendering;

public sealed class SvgTextExtractorTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("mdp-svg-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string WriteSvg(string content, string name = "test.svg")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Extract_ReadsVisibleTextInDocumentOrder()
    {
        string path = WriteSvg(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g><text x="1" y="1">Domain</text></g>
              <text>Application <tspan>Adapters</tspan></text>
              <text>Shell</text>
            </svg>
            """);

        Assert.Equal("Domain Application Adapters Shell", SvgTextExtractor.Extract(path));
    }

    [Fact]
    public void Extract_SkipsInvisibleContainers()
    {
        // The same exclusions the in-page walker applies: a stylesheet's
        // selectors are not text anyone can see.
        string path = WriteSvg(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <title>accessible name</title>
              <desc>long description</desc>
              <style>#mermaid-1 .node { fill: #fff; }</style>
              <script>var x = 1;</script>
              <defs><text>template text</text></defs>
              <metadata>who knows</metadata>
              <text>Visible</text>
            </svg>
            """);

        Assert.Equal("Visible", SvgTextExtractor.Extract(path));
    }

    [Fact]
    public void Extract_ProhibitsDoctypes()
    {
        // A billion-laughs or external-entity payload must die at the reader,
        // not expand. DTDs are prohibited outright, so the file simply yields
        // nothing.
        string path = WriteSvg(
            """
            <!DOCTYPE svg [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;">]>
            <svg xmlns="http://www.w3.org/2000/svg"><text>&b;</text></svg>
            """);

        Assert.Equal(string.Empty, SvgTextExtractor.Extract(path));
    }

    [Fact]
    public void Extract_ToleratesMalformedXml()
    {
        string path = WriteSvg("<svg><text>unclosed");

        // Whatever was readable before the parse failed is acceptable; the
        // contract is only that extraction never throws.
        Exception? recorded = Record.Exception(() => SvgTextExtractor.Extract(path));

        Assert.Null(recorded);
    }

    [Fact]
    public void Extract_ReturnsEmptyForMissingFile()
    {
        string path = Path.Combine(_directory, "not-there.svg");

        Assert.Equal(string.Empty, SvgTextExtractor.Extract(path));
    }

    [Fact]
    public void Extract_RefreshesWhenTheFileChanges()
    {
        string path = WriteSvg("""<svg xmlns="http://www.w3.org/2000/svg"><text>first</text></svg>""");
        Assert.Equal("first", SvgTextExtractor.Extract(path));

        File.WriteAllText(path, """<svg xmlns="http://www.w3.org/2000/svg"><text>second</text></svg>""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));   // defeat timestamp granularity

        Assert.Equal("second", SvgTextExtractor.Extract(path));
    }

    [Fact]
    public void Extract_ServesUnchangedFilesFromTheCache()
    {
        string path = WriteSvg("""<svg xmlns="http://www.w3.org/2000/svg"><text>cached</text></svg>""");

        string first = SvgTextExtractor.Extract(path);

        // Same content and timestamp: the second call must not re-read, which
        // this proves by swapping the bytes while pinning the timestamp.
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, """<svg xmlns="http://www.w3.org/2000/svg"><text>changed</text></svg>""");
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal("cached", first);
        Assert.Equal("cached", SvgTextExtractor.Extract(path));
    }
}
