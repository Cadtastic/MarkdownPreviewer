namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>
/// Remembers which documents the user has explicitly trusted to load resources
/// from the internet.
/// </summary>
/// <remarks>
/// Trust is granted per file, by full path, and survives restarts: the point of
/// the gesture is that the user knows this particular document — usually one
/// they wrote — and does not want to re-authorise it every time they select it
/// in Explorer.
///
/// Trust is a narrow grant. It lifts exactly one restriction: the host's refusal
/// to serve http(s) requests in <c>OnWebResourceRequested</c>. Scripts, frames,
/// forms and outbound <c>connect-src</c> stay blocked by the page's CSP for
/// trusted and untrusted documents alike, so the worst a trusted document can do
/// is tell a remote server that it was opened.
/// </remarks>
public interface ITrustedDocumentStore
{
    /// <summary>
    /// True when the user has trusted <paramref name="fullPath"/>. A null or
    /// blank path is never trusted: an item with no file behind it cannot be
    /// identified again later, so it cannot carry a durable grant.
    /// </summary>
    bool IsTrusted(string? fullPath);

    /// <summary>
    /// Records or withdraws trust for <paramref name="fullPath"/>.
    /// </summary>
    /// <remarks>
    /// Failure is not thrown. A store that cannot be written (a locked hive, a
    /// restricted token) must degrade to "trust did not stick", because the
    /// alternative is breaking the preview over a preference.
    /// </remarks>
    void SetTrusted(string? fullPath, bool trusted);
}
