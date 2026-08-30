namespace MarkdownPreviewer.Infrastructure.Configuration;

/// <summary>Registry locations this extension reads and the installer writes.</summary>
public static class RegistryKeys
{
    /// <summary>CLSID of the Markdown preview handler.</summary>
    /// <remarks>
    /// Changing this value orphans every existing installation's registry
    /// entries. Treat it as immutable for the life of the product.
    /// </remarks>
    public const string PreviewHandlerClsid = "{5B54A6AB-8765-4A71-8732-EA187093A239}";

    /// <summary>Friendly name shown in the shell's preview-handler list.</summary>
    public const string PreviewHandlerName = "Markdown Preview Handler";

    /// <summary>Per-user settings, which take precedence over machine settings.</summary>
    public const string UserSettingsPath = @"Software\MarkdownPreviewer";

    /// <summary>Machine-wide defaults written by the installer.</summary>
    public const string MachineSettingsPath = @"SOFTWARE\MarkdownPreviewer";

    /// <summary>
    /// Per-user list of documents the user has trusted to load remote resources.
    /// </summary>
    /// <remarks>
    /// Deliberately under HKCU only, with no machine-wide counterpart: trust is a
    /// personal judgement about a specific file ("I wrote this one"), not
    /// something an administrator should be able to grant on a user's behalf.
    /// Each value is the document's full path, set to 1. Deleting a value — or
    /// the whole key — revokes the grant, so the list stays inspectable and
    /// clearable with regedit alone.
    /// </remarks>
    public const string TrustedDocumentsPath = UserSettingsPath + @"\TrustedDocuments";

    /// <summary>Where Windows records the light/dark preference for apps.</summary>
    public const string PersonalizePath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Value under <see cref="PersonalizePath"/>: 0 = dark, 1 = light.</summary>
    public const string AppsUseLightThemeValue = "AppsUseLightTheme";
}
