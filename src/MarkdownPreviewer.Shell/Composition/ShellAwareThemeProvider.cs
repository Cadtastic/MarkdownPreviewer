using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Domain.Rendering;
using MarkdownPreviewer.Infrastructure.Configuration;

namespace MarkdownPreviewer.Shell.Composition;

/// <summary>
/// Resolves the theme from, in order: an explicit user setting, the Windows apps
/// colour mode, and finally the background colour the preview host gave us.
/// </summary>
/// <remarks>
/// The third source exists because <c>IPreviewHandlerVisuals.SetBackgroundColor</c>
/// is the only theme signal available in hosts that are not Explorer — Outlook's
/// reading pane, for instance, which has its own light/dark state independent of
/// the shell. Deriving the theme from that colour's luminance is a heuristic, so
/// it sits last, behind two authoritative sources.
/// </remarks>
internal sealed class ShellAwareThemeProvider : IThemeProvider
{
    /// <summary>
    /// Relative-luminance threshold below which we treat the host as dark.
    /// </summary>
    /// <remarks>
    /// 0.5 on the ITU-R BT.601 luma of the host colour. Chosen over a simple
    /// average because green dominates perceived brightness, and Outlook's dark
    /// reading pane is a desaturated near-black that a naive average puts
    /// uncomfortably close to the boundary.
    /// </remarks>
    private const double DarkLuminanceThreshold = 0.5;

    private readonly SystemThemeProvider _system;
    private readonly Func<uint?> _hostBackgroundColour;

    public ShellAwareThemeProvider(SystemThemeProvider system, Func<uint?> hostBackgroundColour)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _hostBackgroundColour = hostBackgroundColour ?? throw new ArgumentNullException(nameof(hostBackgroundColour));
    }

    public AppearanceTheme GetTheme(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.FollowSystemTheme)
        {
            return settings.FixedTheme;
        }

        if (_system.DetectSystemTheme() is { } detected)
        {
            return detected;
        }

        if (_hostBackgroundColour() is { } colorRef)
        {
            return FromColorRef(colorRef);
        }

        return settings.FixedTheme;
    }

    /// <summary>
    /// Maps a Win32 <c>COLORREF</c> (0x00BBGGRR) to a theme.
    /// </summary>
    internal static AppearanceTheme FromColorRef(uint colorRef)
    {
        double red = (colorRef & 0xFF) / 255.0;
        double green = ((colorRef >> 8) & 0xFF) / 255.0;
        double blue = ((colorRef >> 16) & 0xFF) / 255.0;

        double luma = (0.299 * red) + (0.587 * green) + (0.114 * blue);
        return luma < DarkLuminanceThreshold ? AppearanceTheme.Dark : AppearanceTheme.Light;
    }
}
