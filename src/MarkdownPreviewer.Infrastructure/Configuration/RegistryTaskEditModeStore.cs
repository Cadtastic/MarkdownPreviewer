using MarkdownPreviewer.Application.Abstractions;
using Microsoft.Win32;

namespace MarkdownPreviewer.Infrastructure.Configuration;

/// <summary>
/// Keeps the per-document checkbox-editing choice in HKCU, one value per
/// document, alongside the trusted-document list and cleaned up with it by the
/// uninstaller.
/// </summary>
/// <remarks>
/// Same shape and the same reasoning as
/// <see cref="RegistryTrustedDocumentStore"/>: the registry because this code
/// runs inside <c>prevhost.exe</c> under a token we do not control, and
/// readable full paths as value names so the list can be inspected or cleared
/// in regedit without needing this program.
/// </remarks>
public sealed class RegistryTaskEditModeStore(IDiagnosticLog log) : ITaskEditModeStore
{
    public bool IsEnabled(string? fullPath)
    {
        if (!DocumentPathKey.TryNormalise(fullPath, out string key))
        {
            return false;
        }

        try
        {
            using RegistryKey? enabled =
                Registry.CurrentUser.OpenSubKey(RegistryKeys.TaskEditDocumentsPath);

            return enabled?.GetValue(key) switch
            {
                int i => i != 0,
                string s => s is not ("0" or "" or "false" or "False"),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            // Unreadable means "off", which is the safe direction: a preview
            // that will not accept edits is never worse than one that accepts
            // an unintended one.
            log.Debug($"Could not read the task-edit list: {ex.Message}");
            return false;
        }
    }

    public void SetEnabled(string? fullPath, bool enabled)
    {
        if (!DocumentPathKey.TryNormalise(fullPath, out string key))
        {
            log.Debug("Ignoring a task-edit choice for an item with no file behind it.");
            return;
        }

        try
        {
            if (enabled)
            {
                using RegistryKey created =
                    Registry.CurrentUser.CreateSubKey(RegistryKeys.TaskEditDocumentsPath);

                created.SetValue(key, 1, RegistryValueKind.DWord);
            }
            else
            {
                using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(
                    RegistryKeys.TaskEditDocumentsPath, writable: true);

                // Off is the default, so the value is removed rather than set
                // to 0 — the list stays a list of documents that opted in.
                existing?.DeleteValue(key, throwOnMissingValue: false);
            }

            log.Debug($"Checkbox editing for '{key}' is now {(enabled ? "on" : "off")}.");
        }
        catch (Exception ex)
        {
            log.Warn($"Could not record the task-edit choice for '{key}'.", ex);
        }
    }
}
