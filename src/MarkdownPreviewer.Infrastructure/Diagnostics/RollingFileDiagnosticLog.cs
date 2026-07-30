using System.Diagnostics;
using System.Globalization;
using System.Text;
using MarkdownPreviewer.Application.Abstractions;
using Microsoft.Win32;

namespace MarkdownPreviewer.Infrastructure.Diagnostics;

/// <summary>
/// Appends diagnostics to a size-capped file under
/// <c>%LOCALAPPDATA%\MarkdownPreviewer\logs</c>.
/// </summary>
/// <remarks>
/// Off unless <c>HKCU\Software\MarkdownPreviewer\LogLevel</c> is set, because a
/// preview handler runs on every single file selection and an always-on log would
/// be both a performance tax and a privacy problem — it would record the path of
/// every Markdown file the user so much as clicked on.
///
/// Never throws. A logger that can take down the pane it is meant to diagnose is
/// worse than no logger.
/// </remarks>
public sealed class RollingFileDiagnosticLog : IDiagnosticLog, IDisposable
{
    private const long MaximumBytes = 2 * 1024 * 1024;
    private const string LogLevelValueName = "LogLevel";

    private static readonly object Gate = new();

    private readonly DiagnosticLevel _minimumLevel;
    private readonly string? _path;
    private readonly int _processId = Environment.ProcessId;

    private RollingFileDiagnosticLog(DiagnosticLevel minimumLevel, string? path)
    {
        _minimumLevel = minimumLevel;
        _path = path;
    }

    /// <summary>A sink that discards everything, for when logging is disabled.</summary>
    public static IDiagnosticLog Disabled { get; } = new NullDiagnosticLog();

    /// <summary>
    /// Creates the log from configuration, or returns <see cref="Disabled"/>.
    /// </summary>
    public static IDiagnosticLog CreateFromConfiguration()
    {
        try
        {
            DiagnosticLevel? level = ReadConfiguredLevel();
            if (level is null)
            {
                return Disabled;
            }

            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarkdownPreviewer",
                "logs");

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "preview.log");
            return new RollingFileDiagnosticLog(level.Value, path);
        }
        catch
        {
            // Logging must never be the reason a preview fails.
            return Disabled;
        }
    }

    private static DiagnosticLevel? ReadConfiguredLevel()
    {
        using RegistryKey? user = Registry.CurrentUser.OpenSubKey(Configuration.RegistryKeys.UserSettingsPath);
        using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(Configuration.RegistryKeys.MachineSettingsPath);

        object? raw = user?.GetValue(LogLevelValueName) ?? machine?.GetValue(LogLevelValueName);

        return raw switch
        {
            null => null,
            int i when i is >= 0 and <= 3 => (DiagnosticLevel)i,
            string s when Enum.TryParse(s, ignoreCase: true, out DiagnosticLevel parsed) => parsed,
            _ => null,
        };
    }

    public bool IsEnabled(DiagnosticLevel level) => _path is not null && level >= _minimumLevel;

    public void Write(DiagnosticLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        try
        {
            var line = new StringBuilder(256)
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(_processId.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append(']')
                .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(11))
                .Append(message);

            if (exception is not null)
            {
                line.AppendLine().Append("    ").Append(exception.GetType().FullName)
                    .Append(": ").Append(exception.Message);
                if (exception.StackTrace is { Length: > 0 } trace)
                {
                    line.AppendLine().Append(trace);
                }
            }

            lock (Gate)
            {
                RollIfOversized();
                File.AppendAllText(_path!, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Deliberately swallowed. See remarks on the type.
        }
    }

    private void RollIfOversized()
    {
        try
        {
            var info = new FileInfo(_path!);
            if (!info.Exists || info.Length < MaximumBytes)
            {
                return;
            }

            string previous = _path + ".1";
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }

            File.Move(_path!, previous);
        }
        catch
        {
            // If rolling fails the log simply keeps growing; not worth escalating.
        }
    }

    public void Dispose()
    {
        // Nothing retained between writes — append-and-close keeps the file
        // readable while Explorer is still running, which is exactly when you
        // want to read it.
    }

    private sealed class NullDiagnosticLog : IDiagnosticLog
    {
        public bool IsEnabled(DiagnosticLevel level) => false;

        public void Write(DiagnosticLevel level, string message, Exception? exception = null)
        {
            // Still surface to an attached debugger during development.
            if (Debugger.IsAttached)
            {
                Debug.WriteLine($"[MarkdownPreviewer] {level}: {message} {exception}");
            }
        }
    }
}
