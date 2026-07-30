using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Application.Preview;
using MarkdownPreviewer.Domain.Documents;
using MarkdownPreviewer.Domain.Rendering;
using Xunit;

namespace MarkdownPreviewer.Tests.Application;

/// <summary>
/// Exercises the use case with no COM, no WebView2 and no Explorer — which is the
/// entire point of keeping the orchestration in the application layer.
/// </summary>
public sealed class PreviewSessionTests
{
    [Fact]
    public async Task PreviewAsync_RendersTheDocument()
    {
        var surface = new FakeSurface();
        await using var session = NewSession(surface);

        RenderOutcome outcome = await session.PreviewAsync(new FakeReader("# Hi"), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Single(surface.Requests);
        Assert.Equal("# Hi", surface.Requests[0].Document.Source);
    }

    [Fact]
    public async Task PreviewAsync_InitialisesTheSurfaceBeforeRendering()
    {
        var surface = new FakeSurface();
        await using var session = NewSession(surface);

        await session.PreviewAsync(new FakeReader("x"), CancellationToken.None);

        Assert.Equal(["Initialise", "Render"], surface.Calls);
    }

    [Fact]
    public async Task PreviewAsync_ReturnsFailureWhenThereIsNoSource()
    {
        var surface = new FakeSurface();
        await using var session = NewSession(surface);

        RenderOutcome outcome = await session.PreviewAsync(new FakeReader(null), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Empty(surface.Requests);
    }

    [Fact]
    public async Task PreviewAsync_ConvertsAReadFailureIntoAnOutcome()
    {
        // A malformed document must not surface as an exception: the shell would
        // see a COM failure and may disable the pane.
        var surface = new FakeSurface();
        await using var session = NewSession(surface);

        RenderOutcome outcome = await session.PreviewAsync(
            new ThrowingReader(new IOException("disk is on fire")), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("disk is on fire", outcome.FailureMessage);
    }

    [Fact]
    public async Task PreviewAsync_CancelsThePreviousRenderWhenSelectionChanges()
    {
        // Explorer fires selection changes faster than a document with a diagram
        // can render; the older render must be abandoned, not queued.
        var surface = new FakeSurface { BlockUntilCancelled = true };
        await using var session = NewSession(surface);

        Task<RenderOutcome> first = session.PreviewAsync(new FakeReader("first"), CancellationToken.None);
        await surface.RenderStarted.Task;

        surface.BlockUntilCancelled = false;
        RenderOutcome second = await session.PreviewAsync(new FakeReader("second"), CancellationToken.None);

        Assert.True(second.Succeeded);

        RenderOutcome firstOutcome = await first;
        Assert.False(firstOutcome.Succeeded);
    }

    [Fact]
    public async Task PreviewAsync_FallsBackToDefaultsWhenSettingsCannotBeRead()
    {
        // A broken settings store should degrade the preview, never break it.
        var surface = new FakeSurface();
        await using var session = new PreviewSession(
            surface,
            new ThrowingSettingsProvider(),
            new FixedThemeProvider(AppearanceTheme.Light),
            NullLog.Instance);

        RenderOutcome outcome = await session.PreviewAsync(new FakeReader("x"), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(RenderSettings.Default.AllowRawHtml, surface.Requests[0].Settings.AllowRawHtml);
    }

    [Fact]
    public async Task PreviewAsync_AppliesTheResolvedTheme()
    {
        var surface = new FakeSurface();
        await using var session = new PreviewSession(
            surface,
            new FixedSettingsProvider(RenderSettings.Default),
            new FixedThemeProvider(AppearanceTheme.Dark),
            NullLog.Instance);

        await session.PreviewAsync(new FakeReader("x"), CancellationToken.None);

        Assert.Equal(AppearanceTheme.Dark, surface.Requests[0].Theme);
    }

    [Fact]
    public async Task UnloadAsync_ClearsTheSurface()
    {
        var surface = new FakeSurface();
        await using var session = NewSession(surface);

        await session.PreviewAsync(new FakeReader("x"), CancellationToken.None);
        await session.UnloadAsync(CancellationToken.None);

        Assert.Contains("Clear", surface.Calls);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheSurface()
    {
        var surface = new FakeSurface();
        var session = NewSession(surface);

        await session.DisposeAsync();

        Assert.True(surface.Disposed);
    }

    private static PreviewSession NewSession(IPreviewSurface surface) =>
        new(surface,
            new FixedSettingsProvider(RenderSettings.Default),
            new FixedThemeProvider(AppearanceTheme.Light),
            NullLog.Instance);

    // ------------------------------------------------------------- doubles ---

    private sealed class FakeSurface : IPreviewSurface
    {
        public List<string> Calls { get; } = [];

        public List<RenderRequest> Requests { get; } = [];

        public bool Disposed { get; private set; }

        public bool BlockUntilCancelled { get; set; }

        public TaskCompletionSource RenderStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            Calls.Add("Initialise");
            return Task.CompletedTask;
        }

        public async Task<RenderOutcome> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("Render");
            RenderStarted.TrySetResult();

            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            Requests.Add(request);
            return RenderOutcome.Success(TimeSpan.FromMilliseconds(1), false, false, []);
        }

        public Task ApplyThemeAsync(AppearanceTheme theme, CancellationToken cancellationToken)
        {
            Calls.Add("ApplyTheme");
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            Calls.Add("Clear");
            return Task.CompletedTask;
        }

        public void MoveFocusToDocument() => Calls.Add("Focus");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeReader(string? source) : IMarkdownSourceReader
    {
        public bool HasSource => source is not null;

        public Task<MarkdownDocument> ReadAsync(int maximumBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new MarkdownDocument(source!, DocumentLocation.Unknown, false, source!.Length));
    }

    private sealed class ThrowingReader(Exception exception) : IMarkdownSourceReader
    {
        public bool HasSource => true;

        public Task<MarkdownDocument> ReadAsync(int maximumBytes, CancellationToken cancellationToken) =>
            Task.FromException<MarkdownDocument>(exception);
    }

    private sealed class FixedSettingsProvider(RenderSettings settings) : IRenderSettingsProvider
    {
        public RenderSettings GetSettings() => settings;
    }

    private sealed class ThrowingSettingsProvider : IRenderSettingsProvider
    {
        public RenderSettings GetSettings() => throw new InvalidOperationException("registry is unreadable");
    }

    private sealed class FixedThemeProvider(AppearanceTheme theme) : IThemeProvider
    {
        public AppearanceTheme GetTheme(RenderSettings settings) => theme;
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
