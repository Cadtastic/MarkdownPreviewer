using MarkdownPreviewer.Application.Abstractions;
using Microsoft.Win32;

namespace MarkdownPreviewer.Infrastructure.Configuration;

/// <summary>
/// Keeps the trusted-document list in HKCU, one value per trusted path.
/// </summary>
/// <remarks>
/// The registry for the same reason the settings live there: this code runs
/// inside <c>prevhost.exe</c>, a process we do not own and that may hold a
/// restricted token, so a couple of registry calls beat inventing a file store
/// with its own locking and corruption story.
///
/// Paths are stored verbatim as value names. Registry value names may contain
/// backslashes (only key names may not) and compare case-insensitively, which
/// matches how <c>DocumentLocation</c> already compares paths — so the entry
/// written for <c>C:\Docs\Notes.md</c> is found again for
/// <c>c:\docs\notes.md</c>. Storing the readable path rather than a hash is a
/// choice, not an oversight: a user who wants to see or revoke what they have
/// trusted can do it in regedit without needing this program.
/// </remarks>
public sealed class RegistryTrustedDocumentStore(IDiagnosticLog log) : ITrustedDocumentStore
{
    public bool IsTrusted(string? fullPath)
    {
        if (!TryNormalise(fullPath, out string key))
        {
            return false;
        }

        try
        {
            using RegistryKey? trusted =
                Registry.CurrentUser.OpenSubKey(RegistryKeys.TrustedDocumentsPath);

            return trusted?.GetValue(key) switch
            {
                int i => i != 0,
                string s => s is not ("0" or "" or "false" or "False"),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            // An unreadable list means "nothing is trusted", which is the safe
            // direction to fail in.
            log.Debug($"Could not read the trusted-document list: {ex.Message}");
            return false;
        }
    }

    public void SetTrusted(string? fullPath, bool trusted)
    {
        if (!TryNormalise(fullPath, out string key))
        {
            log.Debug("Ignoring a trust change for an item with no file behind it.");
            return;
        }

        try
        {
            if (trusted)
            {
                using RegistryKey created =
                    Registry.CurrentUser.CreateSubKey(RegistryKeys.TrustedDocumentsPath);

                created.SetValue(key, 1, RegistryValueKind.DWord);
                log.Info($"Document trusted for remote resources: {key}");
                return;
            }

            using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(
                RegistryKeys.TrustedDocumentsPath, writable: true);

            // DeleteValue throws when the value was never there; withdrawing
            // trust that was never granted is a no-op, not an error.
            existing?.DeleteValue(key, throwOnMissingValue: false);
            log.Info($"Document trust withdrawn: {key}");
        }
        catch (Exception ex)
        {
            // The preference did not stick. That is worth a log line and
            // nothing more — the preview itself is unaffected.
            log.Warn($"Could not record trust for '{key}'.", ex);
        }
    }

    /// <summary>
    /// Canonicalises a path so the same file cannot be trusted twice under two
    /// spellings, and rejects the stream-fed items that have no file at all.
    /// </summary>
    private static bool TryNormalise(string? fullPath, out string normalised)
    {
        normalised = string.Empty;

        // A display name from IStream.Stat is a bare file name, not a location.
        // Tested before GetFullPath, which would happily root it against the
        // current directory and let one name stand for every file that shares
        // it.
        if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathRooted(fullPath))
        {
            return false;
        }

        try
        {
            normalised = Path.GetFullPath(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
