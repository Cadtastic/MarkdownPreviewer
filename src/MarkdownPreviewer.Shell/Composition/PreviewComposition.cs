using System.Windows.Forms;
using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Application.Preview;
using MarkdownPreviewer.Infrastructure.Assets;
using MarkdownPreviewer.Infrastructure.Configuration;
using MarkdownPreviewer.Infrastructure.Diagnostics;
using MarkdownPreviewer.Infrastructure.Shell;
using MarkdownPreviewer.Rendering.WebView;

namespace MarkdownPreviewer.Shell.Composition;

/// <summary>
/// The composition root. Builds the object graph for one preview handler instance.
/// </summary>
/// <remarks>
/// Hand-wired rather than container-based, deliberately. A DI container would add
/// an assembly load and a few milliseconds of reflection to a code path that runs
/// every time the user presses the down-arrow in Explorer, in exchange for
/// convenience we do not need at this graph size. If the graph grows past roughly
/// a dozen types, revisit.
///
/// <see cref="AssetCatalog"/> is shared process-wide because probing the install
/// directory is pure I/O whose answer cannot change while the process lives.
/// </remarks>
internal static class PreviewComposition
{
    private static readonly Lazy<IWebAssetCatalog> LazyAssetCatalog =
        new(() => new InstallDirectoryAssetCatalog(), isThreadSafe: true);

    private static readonly Lazy<IDiagnosticLog> LazyLog =
        new(RollingFileDiagnosticLog.CreateFromConfiguration, isThreadSafe: true);

    public static IWebAssetCatalog AssetCatalog => LazyAssetCatalog.Value;

    public static IDiagnosticLog Log => LazyLog.Value;

    /// <summary>
    /// Builds a session bound to <paramref name="host"/>.
    /// </summary>
    /// <param name="host">The child window the preview is drawn into.</param>
    /// <param name="hostBackgroundColour">
    /// Supplies the last colour the shell reported through
    /// <c>IPreviewHandlerVisuals</c>, or null if it never did.
    /// </param>
    public static PreviewSession CreateSession(Control host, Func<uint?> hostBackgroundColour)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostBackgroundColour);

        IDiagnosticLog log = Log;

        var launcher = new ShellExecuteLinkLauncher(log);
        var settings = new RegistryRenderSettingsProvider(log);
        var themes = new ShellAwareThemeProvider(new SystemThemeProvider(log), hostBackgroundColour);
        var surface = new WebView2PreviewSurface(host, AssetCatalog, launcher, log);

        return new PreviewSession(surface, settings, themes, log);
    }
}
