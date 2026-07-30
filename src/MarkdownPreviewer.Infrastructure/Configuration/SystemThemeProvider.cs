using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Domain.Rendering;
using Microsoft.Win32;

namespace MarkdownPreviewer.Infrastructure.Configuration;

/// <summary>
/// Resolves the theme from the Windows "Choose your mode" setting.
/// </summary>
/// <remarks>
/// The shell also tells preview handlers what colours to use via
/// <c>IPreviewHandlerVisuals.SetBackgroundColor</c>. We prefer the registry value
/// because it is a clean two-state signal, whereas the visuals callback gives a
/// COLORREF whose luminance we would have to guess a threshold for — and it is
/// not called at all by some hosts. The visuals callback is still honoured as an
/// override; see <c>MarkdownPreviewHandler.SetBackgroundColor</c>.
/// </remarks>
public sealed class SystemThemeProvider : IThemeProvider
{
    private readonly IDiagnosticLog _log;

    public SystemThemeProvider(IDiagnosticLog log) =>
        _log = log ?? throw new ArgumentNullException(nameof(log));

    public AppearanceTheme GetTheme(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.FollowSystemTheme)
        {
            return settings.FixedTheme;
        }

        return DetectSystemTheme() ?? settings.FixedTheme;
    }

    /// <summary>
    /// Reads the apps colour mode. Returns null when the value is absent, which
    /// is the case on Windows editions and SKUs that never wrote it.
    /// </summary>
    public AppearanceTheme? DetectSystemTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeys.PersonalizePath);
            if (key?.GetValue(RegistryKeys.AppsUseLightThemeValue) is int lightMode)
            {
                return lightMode == 0 ? AppearanceTheme.Dark : AppearanceTheme.Light;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not read the system theme: {ex.Message}");
        }

        return null;
    }
}
