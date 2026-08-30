using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Infrastructure.Configuration;
using Microsoft.Win32;
using Xunit;

namespace MarkdownPreviewer.Tests.Infrastructure;

/// <summary>
/// The per-document record of which files have checkbox editing turned on.
/// </summary>
/// <remarks>
/// Exercises the real HKCU key for the same reason the trusted-document tests
/// do: the behaviour worth testing is the registry's. Every path used is under
/// a directory that cannot exist, and the fixture deletes what it wrote.
/// </remarks>
public sealed class RegistryTaskEditModeStoreTests : IDisposable
{
    private readonly List<string> _written = [];

    private string ScratchPath(string name)
    {
        string path = Path.Combine(@"C:\__mdp_tests__", Guid.NewGuid().ToString("N"), name);
        _written.Add(path);
        return path;
    }

    private static RegistryTaskEditModeStore Store() => new(NullLog.Instance);

    [Fact]
    public void EditingIsOffUntilADocumentAsksForIt()
    {
        Assert.False(Store().IsEnabled(ScratchPath("untouched.md")));
    }

    [Fact]
    public void SetEnabled_IsVisibleToALaterRead()
    {
        string path = ScratchPath("enabled.md");
        RegistryTaskEditModeStore store = Store();

        store.SetEnabled(path, true);

        Assert.True(store.IsEnabled(path));
    }

    [Fact]
    public void SetEnabled_TurningItOffForgetsTheDocument()
    {
        string path = ScratchPath("disabled.md");
        RegistryTaskEditModeStore store = Store();
        store.SetEnabled(path, true);

        store.SetEnabled(path, false);

        Assert.False(store.IsEnabled(path));

        // Off is the default, so the document leaves the list rather than
        // sitting in it with a zero.
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeys.TaskEditDocumentsPath);
        Assert.DoesNotContain(path, key?.GetValueNames() ?? []);
    }

    [Fact]
    public void TheChoiceIsPerDocument()
    {
        string enabled = ScratchPath("mine.md");
        string other = ScratchPath("theirs.md");
        RegistryTaskEditModeStore store = Store();

        store.SetEnabled(enabled, true);

        // The whole point of storing this per file: turning it on for one
        // document must not arm the next one the reader opens.
        Assert.True(store.IsEnabled(enabled));
        Assert.False(store.IsEnabled(other));
    }

    [Fact]
    public void IsEnabled_MatchesTheSameFileSpeltDifferently()
    {
        string path = ScratchPath("Case.md");
        RegistryTaskEditModeStore store = Store();
        store.SetEnabled(path, true);

        Assert.True(store.IsEnabled(path.ToUpperInvariant()));
        Assert.True(store.IsEnabled(path.ToLowerInvariant()));
    }

    [Fact]
    public void IsEnabled_TreatsRedundantSegmentsAsTheSameFile()
    {
        string path = ScratchPath("canonical.md");
        RegistryTaskEditModeStore store = Store();
        store.SetEnabled(path, true);

        string roundabout = Path.Combine(Path.GetDirectoryName(path)!, "sub", "..", "canonical.md");

        Assert.True(store.IsEnabled(roundabout));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notes.md")]                 // a display name from IStream.Stat
    [InlineData(@"docs\notes.md")]
    public void ItemsWithNoFileBehindThemAreNeverEnabled(string? candidate)
    {
        RegistryTaskEditModeStore store = Store();

        // Recording is a no-op rather than a throw: a stream-fed item has no
        // identity to store against, and the preview must not break over it.
        store.SetEnabled(candidate, true);

        Assert.False(store.IsEnabled(candidate));
    }

    public void Dispose()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RegistryKeys.TaskEditDocumentsPath, writable: true);

        if (key is null)
        {
            return;
        }

        foreach (string path in _written)
        {
            try { key.DeleteValue(path, throwOnMissingValue: false); }
            catch (Exception) { /* best effort; the value names are synthetic */ }
        }
    }

    private sealed class NullLog : IDiagnosticLog
    {
        public static NullLog Instance { get; } = new();

        public bool IsEnabled(DiagnosticLevel level) => false;

        public void Write(DiagnosticLevel level, string message, Exception? exception = null)
        {
        }
    }
}
