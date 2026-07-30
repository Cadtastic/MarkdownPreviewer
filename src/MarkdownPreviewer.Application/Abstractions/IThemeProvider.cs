using MarkdownPreviewer.Domain.Rendering;

namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>Resolves the colour scheme the preview should use.</summary>
public interface IThemeProvider
{
    /// <summary>
    /// Returns the effective theme, honouring
    /// <see cref="RenderSettings.FollowSystemTheme"/>.
    /// </summary>
    AppearanceTheme GetTheme(RenderSettings settings);
}
