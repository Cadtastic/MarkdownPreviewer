namespace MarkdownPreviewer.Domain.Rendering;

/// <summary>Result reported back by the render surface after a document is drawn.</summary>
/// <param name="Succeeded">False when the surface could not render at all.</param>
/// <param name="Elapsed">Wall-clock time spent rendering.</param>
/// <param name="UsedMermaid">A diagram was drawn.</param>
/// <param name="UsedMath">Math was typeset.</param>
/// <param name="Warnings">Non-fatal problems worth logging.</param>
/// <param name="FailureMessage">Set when <paramref name="Succeeded"/> is false.</param>
public sealed record RenderOutcome(
    bool Succeeded,
    TimeSpan Elapsed,
    bool UsedMermaid,
    bool UsedMath,
    IReadOnlyList<string> Warnings,
    string? FailureMessage)
{
    public static RenderOutcome Success(TimeSpan elapsed, bool usedMermaid, bool usedMath, IReadOnlyList<string> warnings) =>
        new(true, elapsed, usedMermaid, usedMath, warnings, null);

    public static RenderOutcome Failure(string message) =>
        new(false, TimeSpan.Zero, false, false, Array.Empty<string>(), message);
}
