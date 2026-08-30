using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Application.Preview;
using MarkdownPreviewer.Domain.Rendering;
using MarkdownPreviewer.Infrastructure.Assets;
using MarkdownPreviewer.Infrastructure.Configuration;
using MarkdownPreviewer.Infrastructure.Documents;
using MarkdownPreviewer.Infrastructure.Shell;
using MarkdownPreviewer.Rendering.WebView;

namespace MarkdownPreviewer.Harness;

/// <summary>Development host: open a file, watch it render, re-render on change.</summary>
internal sealed class HarnessWindow : Form
{
    private readonly Panel _surfaceHost = new() { Dock = DockStyle.Fill };
    private readonly ToolStrip _toolbar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private readonly StatusStrip _status = new() { Dock = DockStyle.Bottom };
    private readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripComboBox _themeSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly FileSystemWatcher _watcher = new() { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };

    private readonly IDiagnosticLog _log;
    private readonly FileMarkdownSourceReader _reader = new();
    private PreviewSession? _session;
    private WebView2PreviewSurface? _surface;
    private string? _currentPath;
    private System.Windows.Forms.Timer? _debounce;

    public HarnessWindow(string? initialPath)
    {
        Text = "Markdown Preview — development harness";
        MinimumSize = new Size(560, 420);
        Size = new Size(1080, 820);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        _log = new ConsoleDiagnosticLog();

        BuildToolbar();
        BuildStatusBar();

        Controls.Add(_surfaceHost);
        Controls.Add(_toolbar);
        Controls.Add(_status);

        _watcher.Changed += OnFileChanged;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += OnDragDrop;

        Shown += async (_, _) =>
        {
            await BuildSessionAsync().ConfigureAwait(true);
            if (initialPath is { Length: > 0 } && File.Exists(initialPath))
            {
                await OpenAsync(initialPath).ConfigureAwait(true);
            }
            else
            {
                _statusText.Text = "Open a .md file, or drop one onto the window.";
            }
        };
    }

    private void BuildToolbar()
    {
        var open = new ToolStripButton("Open…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        open.Click += async (_, _) => await PromptOpenAsync().ConfigureAwait(true);

        var reload = new ToolStripButton("Reload") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        reload.Click += async (_, _) => await ReRenderAsync().ConfigureAwait(true);

        _themeSelector.Items.AddRange(["System", "Light", "Dark"]);
        _themeSelector.SelectedIndex = 0;
        _themeSelector.SelectedIndexChanged += async (_, _) => await ReRenderAsync().ConfigureAwait(true);

        _toolbar.Items.AddRange(
        [
            open,
            reload,
            new ToolStripSeparator(),
            new ToolStripLabel("Theme:"),
            _themeSelector,
        ]);
    }

    private void BuildStatusBar() => _status.Items.Add(_statusText);

    private async Task BuildSessionAsync()
    {
        var assets = new InstallDirectoryAssetCatalog();
        if (!assets.IsValid)
        {
            _statusText.Text = $"Assets missing under {assets.WebRootPath}: {string.Join(", ", assets.MissingFiles)}";
        }

        _surface = new WebView2PreviewSurface(
            _surfaceHost, assets, new ShellExecuteLinkLauncher(_log),
            new ShellWindowsDocumentRevealer(_log), new RegistryTrustedDocumentStore(_log), _log);
        _session = new PreviewSession(_surface, new HarnessSettingsProvider(_themeSelector), new SystemThemeProvider(_log), _log);

        await _session.PreviewAsync(new EmptyReader(), CancellationToken.None).ConfigureAwait(true);
    }

    private async Task PromptOpenAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Markdown (*.md;*.markdown;*.mdown;*.mkd)|*.md;*.markdown;*.mdown;*.mkd|All files (*.*)|*.*",
            Title = "Open a Markdown file",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await OpenAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    private async Task OpenAsync(string path)
    {
        _currentPath = path;
        _reader.SetFile(path);

        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            _watcher.Path = directory;
            _watcher.Filter = Path.GetFileName(path);
            _watcher.EnableRaisingEvents = true;
        }

        Text = $"{Path.GetFileName(path)} — Markdown Preview harness";
        await ReRenderAsync().ConfigureAwait(true);
    }

    private async Task ReRenderAsync()
    {
        if (_session is null || _currentPath is null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        RenderOutcome outcome = await _session.PreviewAsync(_reader, CancellationToken.None).ConfigureAwait(true);
        stopwatch.Stop();

        _statusText.Text = outcome.Succeeded
            ? $"Rendered in {outcome.Elapsed.TotalMilliseconds:F0} ms " +
              $"(round trip {stopwatch.ElapsedMilliseconds} ms), " +
              $"mermaid={outcome.UsedMermaid}, math={outcome.UsedMath}" +
              (outcome.Warnings.Count > 0 ? $" — {string.Join("; ", outcome.Warnings)}" : string.Empty)
            : $"Failed: {outcome.FailureMessage}";
    }

    /// <summary>
    /// Debounced re-render on save. Editors write in several bursts, so a naive
    /// handler fires three or four renders per Ctrl+S.
    /// </summary>
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        BeginInvoke(() =>
        {
            _debounce?.Stop();
            _debounce?.Dispose();
            _debounce = new System.Windows.Forms.Timer { Interval = 150 };
            _debounce.Tick += async (_, _) =>
            {
                _debounce!.Stop();
                await ReRenderAsync().ConfigureAwait(true);
            };
            _debounce.Start();
        });
    }

    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            await OpenAsync(files[0]).ConfigureAwait(true);
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _watcher.EnableRaisingEvents = false;

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(true);
            _session = null;
        }
    }

    /// <summary>Settings that follow the harness toolbar rather than the registry.</summary>
    private sealed class HarnessSettingsProvider(ToolStripComboBox selector) : IRenderSettingsProvider
    {
        public RenderSettings GetSettings() => selector.SelectedIndex switch
        {
            1 => RenderSettings.Default with { FollowSystemTheme = false, FixedTheme = AppearanceTheme.Light },
            2 => RenderSettings.Default with { FollowSystemTheme = false, FixedTheme = AppearanceTheme.Dark },
            _ => RenderSettings.Default,
        };
    }

    private sealed class EmptyReader : IMarkdownSourceReader
    {
        public bool HasSource => true;

        public Task<Domain.Documents.MarkdownDocument> ReadAsync(int maximumBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new Domain.Documents.MarkdownDocument(
                "# Markdown Preview harness\n\nOpen a file to begin.\n",
                Domain.Documents.DocumentLocation.Unknown,
                wasTruncated: false,
                originalByteCount: 0));
    }

    /// <summary>Writes diagnostics to the debugger and the console.</summary>
    private sealed class ConsoleDiagnosticLog : IDiagnosticLog
    {
        public bool IsEnabled(DiagnosticLevel level) => true;

        public void Write(DiagnosticLevel level, string message, Exception? exception = null)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {level,-11} {message}" +
                          (exception is null ? string.Empty : Environment.NewLine + exception);
            Debug.WriteLine(line);
            Console.WriteLine(line);
        }
    }
}
