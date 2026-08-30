using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using MarkdownPreviewer.Application.Abstractions;

namespace MarkdownPreviewer.Infrastructure.Shell;

/// <summary>
/// Reveals a file by driving the Explorer window that hosts the preview:
/// navigate that same tab to the file's folder, then select the file, which
/// makes it the new preview.
/// </summary>
/// <remarks>
/// <para><b>How the right tab is found.</b> The shell's <c>ShellWindows</c>
/// collection lists every Explorer window and tab, with a top-level window
/// handle and the folder it is showing. Our preview window is a child of the
/// hosting Explorer window, so the tab we want is the entry whose top-level
/// handle is our own root <i>and</i> whose folder is the one the previewed
/// document lives in — the folder match is what tells the hosting tab apart
/// from the same window's other tabs. If no entry matches both (the harness,
/// Outlook's reading pane, a document inside a .zip), the fallback is
/// <c>SHOpenFolderAndSelectItems</c>, which reuses or opens a folder window.</para>
///
/// <para><b>Why a dedicated thread.</b> Changing Explorer's selection is the
/// point of this class — and the moment it happens, Explorer tears down the
/// preview handler whose click started it. Every step therefore runs on a
/// short-lived STA thread owned by nobody: the handler can die mid-reveal and
/// the navigation still completes.</para>
///
/// <para><b>Why late-bound COM.</b> The shell automation objects are IDispatch
/// interfaces designed for scripting; reflection over the ProgID needs no
/// interop assemblies, no code generator, and marshals safely across to
/// Explorer's process — the same calls a PowerShell one-liner would make.</para>
/// </remarks>
public sealed class ShellWindowsDocumentRevealer(IDiagnosticLog log) : IDocumentRevealer
{
    /// <summary>How long a cross-folder navigation may take before giving up.</summary>
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(4);

    /// <summary>How long to keep retrying the selection after navigation lands.</summary>
    private static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(2);

    /// <summary>SVSI flags: select, deselect others, ensure visible, give focus.</summary>
    private const int SelectFlags = 0x1D;

    private const uint GaRoot = 2;

    public void Reveal(string fullPath, nint hostWindow, string? previewedDirectory)
    {
        // The caller is a preview handler that this reveal is about to destroy;
        // see the class remarks. The thread is background so it can never keep
        // prevhost alive on its own.
        var worker = new Thread(() => RevealCore(fullPath, hostWindow, previewedDirectory))
        {
            IsBackground = true,
            Name = "MarkdownPreviewer.Reveal",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private void RevealCore(string fullPath, nint hostWindow, string? previewedDirectory)
    {
        try
        {
            bool isDirectory = Directory.Exists(fullPath);
            string targetFolder = isDirectory
                ? fullPath
                : Path.GetDirectoryName(fullPath) ?? fullPath;

            object? tab = FindHostTab(hostWindow, previewedDirectory);
            if (tab is null)
            {
                log.Info($"No Explorer tab hosts this preview; opening a folder window for '{fullPath}'.");
                OpenFolderAndSelect(fullPath);
                return;
            }

            // The common case — a README linking to a sibling — needs no
            // navigation at all: the tab is already showing the right folder,
            // and moving the selection is the whole job.
            if (!PathsEqual(GetLocationPath(tab), targetFolder))
            {
                Invoke(tab, "Navigate2", targetFolder);

                if (!WaitForLocation(tab, targetFolder))
                {
                    log.Warn($"Explorer did not reach '{targetFolder}' in time; opening a folder window instead.");
                    OpenFolderAndSelect(fullPath);
                    return;
                }
            }

            if (isDirectory)
            {
                log.Info($"Navigated Explorer into '{fullPath}'.");
                return;
            }

            if (SelectWithRetry(tab, fullPath))
            {
                log.Info($"Revealed '{fullPath}' in the hosting Explorer tab.");
            }
            else
            {
                // Navigation landed, so the reader is in the right folder and
                // only the selection is missing. Opening a second window now
                // would be worse than the miss.
                log.Warn($"Could not select '{fullPath}' after navigating; the folder is showing, the selection is not.");
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Revealing '{fullPath}' in Explorer failed.", ex);
            try { OpenFolderAndSelect(fullPath); }
            catch (Exception fallbackEx) { log.Debug($"Folder-window fallback also failed: {fallbackEx.Message}"); }
        }
    }

    /// <summary>
    /// Finds the ShellWindows entry for the Explorer tab hosting the preview,
    /// or null when the preview is not hosted by Explorer at all.
    /// </summary>
    private static object? FindHostTab(nint hostWindow, string? previewedDirectory)
    {
        if (hostWindow == 0 || previewedDirectory is null)
        {
            return null;
        }

        nint ourRoot = GetAncestor(hostWindow, GaRoot);
        if (ourRoot == 0)
        {
            return null;
        }

        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return null;
        }

        object? shell = Activator.CreateInstance(shellType);
        if (shell is null)
        {
            return null;
        }

        object? windows = Invoke(shell, "Windows");
        if (windows is null)
        {
            return null;
        }

        int count = Convert.ToInt32(Get(windows, "Count"), CultureInfo.InvariantCulture);
        for (int i = 0; i < count; i++)
        {
            object? item = Invoke(windows, "Item", i);
            if (item is null)
            {
                continue;   // windows can close between Count and Item
            }

            try
            {
                nint itemRoot = GetAncestor(
                    (nint)Convert.ToInt64(Get(item, "HWND"), CultureInfo.InvariantCulture), GaRoot);

                // Both conditions carry weight: the root ties the entry to the
                // window our preview lives in, and the folder ties it to the
                // hosting TAB — every tab of a window shares the same root.
                if (itemRoot == ourRoot && PathsEqual(GetLocationPath(item), previewedDirectory))
                {
                    return item;
                }
            }
            catch (Exception)
            {
                // A window that closed mid-enumeration; skip it.
            }
        }

        return null;
    }

    private static bool WaitForLocation(object tab, string targetFolder)
    {
        DateTime deadline = DateTime.UtcNow + NavigationTimeout;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);

            try
            {
                if (PathsEqual(GetLocationPath(tab), targetFolder))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // Mid-navigation the automation object can transiently refuse;
                // that is what the polling is for.
            }
        }

        return false;
    }

    /// <summary>
    /// Selects the file, verifying the selection took. Retried because the view
    /// keeps initialising for a moment after the location already reports the
    /// new folder, and a SelectItem in that window is silently ignored.
    /// </summary>
    private static bool SelectWithRetry(object tab, string fullPath)
    {
        DateTime deadline = DateTime.UtcNow + SelectionTimeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                object? view = Get(tab, "Document");
                if (view is not null)
                {
                    Invoke(view, "SelectItem", fullPath, SelectFlags);

                    object? selected = Invoke(view, "SelectedItems");
                    if (selected is not null &&
                        Convert.ToInt32(Get(selected, "Count"), CultureInfo.InvariantCulture) > 0)
                    {
                        object? first = Invoke(selected, "Item", 0);
                        if (first is not null &&
                            PathsEqual(Get(first, "Path") as string, fullPath))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // The view is still settling; try again.
            }

            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>The tab's current folder as a local path, or null for virtual views.</summary>
    private static string? GetLocationPath(object tab) =>
        TryGetLocalPath(Get(tab, "LocationURL") as string);

    /// <summary>
    /// Converts a ShellWindows <c>LocationURL</c> ("file:///C:/Docs%20Etc") to a
    /// local path. Null for anything that is not a file-system folder — search
    /// results, libraries, control panel — which correctly never matches.
    /// </summary>
    internal static string? TryGetLocalPath(string? locationUrl)
    {
        if (string.IsNullOrEmpty(locationUrl))
        {
            return null;
        }

        return Uri.TryCreate(locationUrl, UriKind.Absolute, out Uri? uri) && uri.IsFile
            ? uri.LocalPath
            : null;
    }

    /// <summary>Case-insensitive path equality, indifferent to trailing separators.</summary>
    internal static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The out-of-band fallback: a folder window (reused when one already shows
    /// the folder) with the item selected. Not the same tab, but the file is on
    /// screen and previewable, which is the promise being kept.
    /// </summary>
    private static void OpenFolderAndSelect(string fullPath)
    {
        int hr = SHParseDisplayName(fullPath, 0, out nint pidl, 0, out _);
        if (hr < 0 || pidl == 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            // With no child array, the item itself is selected in its parent.
            hr = SHOpenFolderAndSelectItems(pidl, 0, 0, 0);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    // ------------------------------------------------------- late-bound COM ---

    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(
            property, BindingFlags.GetProperty, binder: null, target, args: null,
            modifiers: null, CultureInfo.InvariantCulture, namedParameters: null);

    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(
            method, BindingFlags.InvokeMethod, binder: null, target, args,
            modifiers: null, CultureInfo.InvariantCulture, namedParameters: null);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name, nint bindingContext, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        nint pidlFolder, uint itemCount, nint items, uint flags);
}
