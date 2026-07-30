namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>Locates the bundled HTML/CSS/JS render assets on disk.</summary>
public interface IWebAssetCatalog
{
    /// <summary>Absolute path to the directory containing <c>index.html</c>.</summary>
    string WebRootPath { get; }

    /// <summary>True when the asset tree is present and complete.</summary>
    bool IsValid { get; }

    /// <summary>Names of expected files that are missing, for diagnostics.</summary>
    IReadOnlyList<string> MissingFiles { get; }
}
