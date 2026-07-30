using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Domain.Rendering;
using Microsoft.Win32;

namespace MarkdownPreviewer.Infrastructure.Configuration;

/// <summary>
/// Reads settings from HKCU, falling back to HKLM, falling back to defaults.
/// </summary>
/// <remarks>
/// Registry rather than a JSON file on purpose: the extension is loaded into
/// <c>prevhost.exe</c>, a process we do not control and that may be running under
/// a restricted token. Reading a couple of registry values is cheap, needs no
/// file-system probing, and lets a machine administrator set defaults via Group
/// Policy without us writing any policy plumbing.
///
/// Every read is defensive. A garbage value must degrade one setting, not the
/// whole preview.
/// </remarks>
public sealed class RegistryRenderSettingsProvider : IRenderSettingsProvider
{
    private readonly IDiagnosticLog _log;

    public RegistryRenderSettingsProvider(IDiagnosticLog log) =>
        _log = log ?? throw new ArgumentNullException(nameof(log));

    public RenderSettings GetSettings()
    {
        try
        {
            using RegistryKey? user = Registry.CurrentUser.OpenSubKey(RegistryKeys.UserSettingsPath);
            using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(RegistryKeys.MachineSettingsPath);

            if (user is null && machine is null)
            {
                return RenderSettings.Default;
            }

            RenderSettings defaults = RenderSettings.Default;

            return new RenderSettings
            {
                AllowRawHtml      = Bool(user, machine, "AllowRawHtml",      defaults.AllowRawHtml),
                AllowRemoteImages = Bool(user, machine, "AllowRemoteImages", defaults.AllowRemoteImages),
                Linkify           = Bool(user, machine, "Linkify",           defaults.Linkify),
                Typographer       = Bool(user, machine, "Typographer",       defaults.Typographer),
                Highlight         = Bool(user, machine, "Highlight",         defaults.Highlight),
                Mermaid           = Bool(user, machine, "Mermaid",           defaults.Mermaid),
                Math              = Bool(user, machine, "Math",              defaults.Math),
                SingleDollarMath  = Bool(user, machine, "SingleDollarMath",  defaults.SingleDollarMath),
                TaskLists         = Bool(user, machine, "TaskLists",         defaults.TaskLists),
                ShowFrontMatter   = Bool(user, machine, "ShowFrontMatter",   defaults.ShowFrontMatter),
                FollowSystemTheme = Bool(user, machine, "FollowSystemTheme", defaults.FollowSystemTheme),
                FixedTheme        = Theme(user, machine, defaults.FixedTheme),
                FontScalePercent  = Int(user, machine, "FontScalePercent",   defaults.FontScalePercent),
                MaximumBytes      = Int(user, machine, "MaximumBytes",       defaults.MaximumBytes),
            }.Normalised();
        }
        catch (Exception ex)
        {
            _log.Warn("Reading settings from the registry failed; using defaults.", ex);
            return RenderSettings.Default;
        }
    }

    private bool Bool(RegistryKey? user, RegistryKey? machine, string name, bool fallback)
    {
        int? value = RawInt(user, name) ?? RawInt(machine, name);
        return value is null ? fallback : value.Value != 0;
    }

    private int Int(RegistryKey? user, RegistryKey? machine, string name, int fallback) =>
        RawInt(user, name) ?? RawInt(machine, name) ?? fallback;

    private static AppearanceTheme Theme(RegistryKey? user, RegistryKey? machine, AppearanceTheme fallback)
    {
        string? raw = user?.GetValue("FixedTheme") as string
                   ?? machine?.GetValue("FixedTheme") as string;

        return raw is null
            ? fallback
            : Enum.TryParse(raw, ignoreCase: true, out AppearanceTheme parsed) ? parsed : fallback;
    }

    private int? RawInt(RegistryKey? key, string name)
    {
        if (key is null)
        {
            return null;
        }

        try
        {
            object? value = key.GetValue(name);
            return value switch
            {
                int i => i,
                long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
                // REG_SZ holding a number: tolerate it, people edit by hand.
                string s when int.TryParse(s, out int parsed) => parsed,
                string s when bool.TryParse(s, out bool flag) => flag ? 1 : 0,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.Debug($"Ignoring unreadable setting '{name}': {ex.Message}");
            return null;
        }
    }
}
