using System.Diagnostics;
using MarkdownPreviewer.Application.Abstractions;

namespace MarkdownPreviewer.Infrastructure.Shell;

/// <summary>
/// Opens http/https/mailto links in the user's default handler.
/// </summary>
/// <remarks>
/// This is the extension's only outbound action taken on behalf of untrusted
/// content, so it re-validates the URL rather than trusting the page. The page's
/// click handler already filters schemes; doing it again here means a bug or an
/// injected script on the page side cannot turn a preview into an arbitrary
/// process launch. Defence in depth on a two-line method is cheap.
/// </remarks>
public sealed class ShellExecuteLinkLauncher : IExternalLinkLauncher
{
    private readonly IDiagnosticLog _log;

    public ShellExecuteLinkLauncher(IDiagnosticLog log) =>
        _log = log ?? throw new ArgumentNullException(nameof(log));

    public void Launch(string url)
    {
        if (!IsPermitted(url, out Uri? parsed))
        {
            _log.Warn($"Refused to open a link with an unsupported scheme: {Describe(url)}");
            return;
        }

        try
        {
            // UseShellExecute routes through the registered protocol handler.
            // AbsoluteUri (not the raw string) ensures what we hand off is the
            // parsed, normalised form we just validated.
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = parsed!.AbsoluteUri,
                UseShellExecute = true,
            });

            _log.Info($"Opened external link: {parsed.Scheme}://{parsed.Host}");
        }
        catch (Exception ex)
        {
            _log.Warn("Could not open the link.", ex);
        }
    }

    /// <summary>
    /// File types a linked document may open as. Deliberately an allowlist of
    /// inert document formats: no executables, no scripts, no installers, no
    /// shortcuts — a hostile README must not be able to phrase "click here" as a
    /// process launch.
    /// </summary>
    private static readonly HashSet<string> OpenableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdwn", ".mdtxt", ".mdtext",
        ".txt", ".log", ".json", ".yaml", ".yml", ".toml", ".xml", ".csv", ".tsv",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif",
        ".pdf",
    };

    public void LaunchDocument(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) ||
            !Path.IsPathFullyQualified(fullPath) ||
            !OpenableExtensions.Contains(Path.GetExtension(fullPath)) ||
            !File.Exists(fullPath))
        {
            _log.Warn($"Refused to open a linked file: {Describe(fullPath ?? string.Empty)}");
            return;
        }

        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true,
            });

            _log.Info($"Opened linked document: {Path.GetFileName(fullPath)}");
        }
        catch (Exception ex)
        {
            _log.Warn("Could not open the linked document.", ex);
        }
    }

    internal static bool IsPermitted(string url, out Uri? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? candidate))
        {
            return false;
        }

        bool permitted =
            candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            candidate.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);

        if (!permitted)
        {
            return false;
        }

        parsed = candidate;
        return true;
    }

    /// <summary>Truncates a rejected URL so we do not dump attacker-controlled text into the log.</summary>
    private static string Describe(string url) =>
        url.Length <= 64 ? url : string.Concat(url.AsSpan(0, 64), "…");
}
