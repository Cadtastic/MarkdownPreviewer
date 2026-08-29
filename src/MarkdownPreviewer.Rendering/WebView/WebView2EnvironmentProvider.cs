using MarkdownPreviewer.Application.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace MarkdownPreviewer.Rendering.WebView;

/// <summary>
/// Creates and caches the process-wide <see cref="CoreWebView2Environment"/>.
/// </summary>
/// <remarks>
/// Two things here are not optional.
///
/// <para><b>The user data folder must be set explicitly.</b> WebView2 defaults to
/// a folder beside the host executable. Our host executable is
/// <c>C:\Windows\System32\prevhost.exe</c>, which is not writable, so the default
/// fails outright — and the failure surfaces as a blank pane with no error. We
/// point it at <c>%LOCALAPPDATA%</c> instead.</para>
///
/// <para><b>The environment is cached per process.</b> <c>prevhost.exe</c> is
/// reused across selections and can host several handler instances at once.
/// Creating an environment per instance would both waste a browser process each
/// time and risk contending on the same user data folder.</para>
/// </remarks>
internal static class WebView2EnvironmentProvider
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CoreWebView2Environment? _environment;

    /// <summary>
    /// Forgets the cached environment so the next preview builds a fresh one.
    /// </summary>
    /// <remarks>
    /// A cached environment is only as alive as the browser process behind it.
    /// When that process goes away — an Evergreen runtime update swapping the
    /// installation out from under us, a crash, the shell reaping it — creating
    /// a controller from the stale environment fails with
    /// <c>ERROR_INVALID_STATE</c> (0x8007139F), and it keeps failing for the
    /// life of the surrogate because the cache never noticed. Discarding is
    /// cheap; the next <see cref="GetAsync"/> re-resolves the current runtime.
    /// </remarks>
    public static void Discard()
    {
        _environment = null;
    }

    public static async Task<CoreWebView2Environment> GetAsync(IDiagnosticLog log, CancellationToken cancellationToken)
    {
        if (_environment is not null)
        {
            return _environment;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_environment is not null)
            {
                return _environment;
            }

            string userDataFolder = ResolveUserDataFolder();
            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions
            {
                // Locale follows the user; affects only WebView2's own UI strings.
                Language = System.Globalization.CultureInfo.CurrentUICulture.Name,

                // A preview pane never needs a single-sign-on token, and it should
                // not be able to reach the network at all (CSP already says
                // connect-src 'none'; this is belt and braces at the browser level).
                AdditionalBrowserArguments = string.Join(' ',
                    "--disable-background-networking",
                    "--disable-component-update",
                    "--disable-sync",
                    "--no-first-run",
                    "--disable-features=Translate,OptimizationHints,MediaRouter"),
            };

            log.Info($"Creating the WebView2 environment (user data folder: {userDataFolder}).");

            CoreWebView2Environment created = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder, options: options)
                .ConfigureAwait(true);

            // Raised even when no controller is open, which is exactly the
            // window in which a browser death would otherwise go unnoticed and
            // poison every later preview in this process.
            created.BrowserProcessExited += (_, e) =>
            {
                log.Warn($"The WebView2 browser process exited ({e.BrowserProcessExitKind}); " +
                         "discarding the cached environment.");
                if (ReferenceEquals(_environment, created))
                {
                    Discard();
                }
            };

            _environment = created;
            log.Info($"WebView2 runtime version {created.BrowserVersionString}.");
            return created;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string ResolveUserDataFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarkdownPreviewer",
            "WebView2");
}
