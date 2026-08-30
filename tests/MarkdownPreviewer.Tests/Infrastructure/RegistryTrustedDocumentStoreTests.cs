using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Infrastructure.Configuration;
using Microsoft.Win32;
using Xunit;

namespace MarkdownPreviewer.Tests.Infrastructure;

/// <summary>
/// Exercises the real HKCU key, because the behaviour worth testing here is
/// exactly the registry's: whether a grant survives a round trip, and whether a
/// path that differs only in case still finds it.
/// </summary>
/// <remarks>
/// Every path used is under a directory that cannot exist, and the fixture
/// deletes each value it wrote — so a run leaves the user's own trusted
/// documents untouched.
/// </remarks>
public sealed class RegistryTrustedDocumentStoreTests : IDisposable
{
    private readonly List<string> _written = [];

    private string ScratchPath(string name)
    {
        string path = Path.Combine(@"C:\__mdp_tests__", Guid.NewGuid().ToString("N"), name);
        _written.Add(path);
        return path;
    }

    private static RegistryTrustedDocumentStore Store() =>
        new(NullLog.Instance);

    [Fact]
    public void IsTrusted_IsFalseForADocumentNobodyHasTrusted()
    {
        Assert.False(Store().IsTrusted(ScratchPath("never-granted.md")));
    }

    [Fact]
    public void SetTrusted_GrantIsVisibleToALaterRead()
    {
        string path = ScratchPath("granted.md");
        RegistryTrustedDocumentStore store = Store();

        store.SetTrusted(path, true);

        Assert.True(store.IsTrusted(path));
    }

    [Fact]
    public void SetTrusted_WithdrawalRemovesTheGrant()
    {
        string path = ScratchPath("withdrawn.md");
        RegistryTrustedDocumentStore store = Store();
        store.SetTrusted(path, true);

        store.SetTrusted(path, false);

        Assert.False(store.IsTrusted(path));
    }

    [Fact]
    public void SetTrusted_WithdrawingAGrantThatWasNeverMadeIsNotAnError()
    {
        RegistryTrustedDocumentStore store = Store();

        store.SetTrusted(ScratchPath("absent.md"), false);
    }

    [Fact]
    public void IsTrusted_MatchesTheSameFileSpeltDifferently()
    {
        // Windows paths are case-insensitive, so a second spelling of the same
        // file must find the same grant rather than miss it.
        string path = ScratchPath("Case.md");
        RegistryTrustedDocumentStore store = Store();
        store.SetTrusted(path, true);

        Assert.True(store.IsTrusted(path.ToUpperInvariant()));
        Assert.True(store.IsTrusted(path.ToLowerInvariant()));
    }

    [Fact]
    public void IsTrusted_TreatsRedundantSegmentsAsTheSameFile()
    {
        string path = ScratchPath("canonical.md");
        RegistryTrustedDocumentStore store = Store();
        store.SetTrusted(path, true);

        string roundabout = Path.Combine(Path.GetDirectoryName(path)!, "sub", "..", "canonical.md");

        Assert.True(store.IsTrusted(roundabout));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notes.md")]                 // a display name from IStream.Stat
    [InlineData(@"docs\notes.md")]
    public void UnlocatableItemsCanNeverBeTrusted(string? candidate)
    {
        RegistryTrustedDocumentStore store = Store();

        // Granting is a no-op rather than a throw: a stream-fed item has no
        // identity to record, and the preview must not break over it.
        store.SetTrusted(candidate, true);

        Assert.False(store.IsTrusted(candidate));
    }

    public void Dispose()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RegistryKeys.TrustedDocumentsPath, writable: true);

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
