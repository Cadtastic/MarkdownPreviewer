using System.Reflection;
using MarkdownPreviewer.Application.Abstractions;

namespace MarkdownPreviewer.Infrastructure.Assets;

/// <summary>
/// Locates the bundled web assets relative to the loaded assembly.
/// </summary>
/// <remarks>
/// <see cref="Assembly.Location"/> — not the current working directory and not
/// <c>AppContext.BaseDirectory</c>-by-luck. Inside <c>prevhost.exe</c> the working
/// directory is arbitrary (often <c>C:\Windows\system32</c>), so any relative
/// probing silently resolves to the wrong place and the pane renders blank with no
/// obvious cause. This class exists mainly to make that failure loud.
/// </remarks>
public sealed class InstallDirectoryAssetCatalog : IWebAssetCatalog
{
    private static readonly string[] RequiredFiles =
    [
        "index.html",
        Path.Combine("css", "github-markdown-light.css"),
        Path.Combine("css", "github-markdown-dark.css"),
        Path.Combine("css", "github.min.css"),
        Path.Combine("css", "github-dark.min.css"),
        Path.Combine("css", "preview.css"),
        Path.Combine("js", "markdown-it.min.js"),
        Path.Combine("js", "markdownItAnchor.umd.js"),
        Path.Combine("js", "highlight.min.js"),
        Path.Combine("js", "preview.js"),
        // mermaid.min.js, mathjax-config.js and tex-mml-svg.js are loaded on
        // demand; their absence degrades a feature rather than breaking startup,
        // so they are checked separately.
    ];

    private static readonly string[] OptionalFiles =
    [
        Path.Combine("js", "mermaid.min.js"),
        Path.Combine("js", "mathjax-config.js"),
        Path.Combine("js", "tex-mml-svg.js"),
    ];

    private readonly List<string> _missing = [];

    public InstallDirectoryAssetCatalog()
    {
        WebRootPath = ResolveWebRoot();

        foreach (string relative in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(WebRootPath, relative)))
            {
                _missing.Add(relative);
            }
        }

        IsValid = _missing.Count == 0;

        foreach (string relative in OptionalFiles)
        {
            if (!File.Exists(Path.Combine(WebRootPath, relative)))
            {
                _missing.Add(relative + " (optional)");
            }
        }
    }

    public string WebRootPath { get; }

    public bool IsValid { get; }

    public IReadOnlyList<string> MissingFiles => _missing;

    private static string ResolveWebRoot()
    {
        string assemblyDirectory =
            Path.GetDirectoryName(typeof(InstallDirectoryAssetCatalog).Assembly.Location)
            ?? AppContext.BaseDirectory;

        // Installed layout:  <install>\MarkdownPreviewer.Shell.dll + <install>\assets\web
        // Dev layout:        ...\bin\x64\Debug\net8.0-windows\ + repo\assets\web
        string[] candidates =
        [
            Path.Combine(assemblyDirectory, "assets", "web"),
            Path.Combine(assemblyDirectory, "web"),
            Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "..", "..", "..", "assets", "web")),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }
        }

        // Return the expected production path so diagnostics name a useful
        // location rather than whichever probe happened to be last.
        return candidates[0];
    }
}
