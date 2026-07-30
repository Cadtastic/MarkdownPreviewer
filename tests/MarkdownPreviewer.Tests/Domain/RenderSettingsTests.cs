using MarkdownPreviewer.Domain.Rendering;
using Xunit;

namespace MarkdownPreviewer.Tests.Domain;

public sealed class RenderSettingsTests
{
    [Fact]
    public void Default_IsSafeForUntrustedDocuments()
    {
        RenderSettings settings = RenderSettings.Default;

        // Security/usability decisions, not arbitrary defaults; a regression on
        // any of these is a behaviour change worth failing a build over.
        //
        // Raw HTML defaults ON because what renders is the sanitised form
        // (scripts, frames, forms, handlers and dangerous schemes stripped, CSP
        // behind it) — but remote images stay OFF: they are how tracking pixels
        // learn that this user looked at this file.
        Assert.True(settings.AllowRawHtml);
        Assert.False(settings.AllowRemoteImages);
        Assert.False(settings.SingleDollarMath);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(49, 50)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(300, 300)]
    [InlineData(5000, 300)]
    [InlineData(-1, 50)]
    public void Normalised_ClampsFontScale(int input, int expected)
    {
        RenderSettings settings = (RenderSettings.Default with { FontScalePercent = input }).Normalised();

        Assert.Equal(expected, settings.FontScalePercent);
    }

    [Theory]
    [InlineData(0, 64 * 1024)]
    [InlineData(1024, 64 * 1024)]
    [InlineData(4 * 1024 * 1024, 4 * 1024 * 1024)]
    [InlineData(int.MaxValue, 64 * 1024 * 1024)]
    public void Normalised_ClampsMaximumBytes(int input, int expected)
    {
        RenderSettings settings = (RenderSettings.Default with { MaximumBytes = input }).Normalised();

        Assert.Equal(expected, settings.MaximumBytes);
    }

    [Fact]
    public void Normalised_LeavesEverythingElseAlone()
    {
        RenderSettings original = RenderSettings.Default with
        {
            AllowRawHtml = true,
            Mermaid = false,
            FixedTheme = AppearanceTheme.Dark,
            FollowSystemTheme = false,
        };

        RenderSettings normalised = original.Normalised();

        Assert.True(normalised.AllowRawHtml);
        Assert.False(normalised.Mermaid);
        Assert.Equal(AppearanceTheme.Dark, normalised.FixedTheme);
        Assert.False(normalised.FollowSystemTheme);
    }
}
