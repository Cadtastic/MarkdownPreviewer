namespace MarkdownPreviewer.Domain.Rendering;

/// <summary>
/// User-controllable rendering behaviour. Immutable; validated on construction.
/// </summary>
/// <remarks>
/// Defaults are chosen for a passive previewer over untrusted input, which is a
/// different threat model from an editor the user typed the document into:
/// <list type="bullet">
///   <item><see cref="AllowRawHtml"/> is on, but what renders is the sanitised
///   form: scripts, frames, forms, event handlers and dangerous URL schemes are
///   stripped, and CSP forbids inline script and network access independently.
///   GitHub-style READMEs lean heavily on raw HTML (centering, badges,
///   <c>&lt;details&gt;</c>), and showing them as escaped source reads as
///   broken.</item>
///   <item><see cref="AllowRemoteImages"/> is off — remote images are how
///   tracking pixels work, and a previewer must not leak "this user looked at
///   this file" to the network. Enabling it is a per-user choice.</item>
///   <item><see cref="SingleDollarMath"/> is off — enabling <c>$…$</c> makes
///   ordinary prose about money ("costs $5, sometimes $10") render as
///   mathematics, which is a worse failure than math not rendering.</item>
/// </list>
/// </remarks>
public sealed record RenderSettings
{
    public static RenderSettings Default { get; } = new();

    /// <summary>Render (sanitised) raw HTML embedded in the Markdown.</summary>
    public bool AllowRawHtml { get; init; } = true;

    /// <summary>
    /// Allow images from http(s) hosts. Off by default; see remarks on the type.
    /// </summary>
    public bool AllowRemoteImages { get; init; }

    /// <summary>Auto-link bare URLs.</summary>
    public bool Linkify { get; init; } = true;

    /// <summary>Smart quotes, dashes and ellipses.</summary>
    public bool Typographer { get; init; }

    /// <summary>Syntax-highlight fenced code blocks.</summary>
    public bool Highlight { get; init; } = true;

    /// <summary>Render <c>```mermaid</c> fences as diagrams.</summary>
    public bool Mermaid { get; init; } = true;

    /// <summary>Typeset LaTeX math.</summary>
    public bool Math { get; init; } = true;

    /// <summary>Treat <c>$…$</c> as inline math. Off by default; see remarks on the type.</summary>
    public bool SingleDollarMath { get; init; }

    /// <summary>Render <c>- [ ]</c> / <c>- [x]</c> as checkboxes.</summary>
    public bool TaskLists { get; init; } = true;

    /// <summary>Show YAML/TOML front matter in a collapsed block instead of discarding it.</summary>
    public bool ShowFrontMatter { get; init; } = true;

    /// <summary>Follow the Windows apps colour mode instead of pinning a theme.</summary>
    public bool FollowSystemTheme { get; init; } = true;

    /// <summary>Theme used when <see cref="FollowSystemTheme"/> is false.</summary>
    public AppearanceTheme FixedTheme { get; init; } = AppearanceTheme.Light;

    /// <summary>Base font size as a percentage. Clamped to 50–300.</summary>
    public int FontScalePercent { get; init; } = 100;

    /// <summary>Largest file that will be read in full.</summary>
    public int MaximumBytes { get; init; } = Documents.MarkdownDocument.DefaultMaximumBytes;

    /// <summary>
    /// Returns a copy with every value forced into its supported range.
    /// </summary>
    /// <remarks>
    /// Settings arrive from the registry, where anyone can write anything.
    /// Clamping rather than throwing means a bad value degrades the preview
    /// instead of breaking it.
    /// </remarks>
    public RenderSettings Normalised() => this with
    {
        // Fully qualified: the `Math` property on this record shadows the
        // System.Math type inside the declaring type's scope.
        FontScalePercent = System.Math.Clamp(FontScalePercent, 50, 300),
        MaximumBytes = System.Math.Clamp(MaximumBytes, 64 * 1024, 64 * 1024 * 1024),
    };
}
