namespace MarkdownPreviewer.Application.Abstractions;

/// <summary>Severity of a diagnostic message.</summary>
public enum DiagnosticLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Write-only diagnostics sink.
/// </summary>
/// <remarks>
/// A shell extension has no console, no stdout and no window of its own to
/// complain in. When a preview pane goes blank, a log file is the only way to
/// find out why — so logging here is load-bearing, not decoration.
/// Implementations must swallow their own failures.
/// </remarks>
public interface IDiagnosticLog
{
    bool IsEnabled(DiagnosticLevel level);

    void Write(DiagnosticLevel level, string message, Exception? exception = null);
}

/// <summary>Convenience wrappers over <see cref="IDiagnosticLog.Write"/>.</summary>
public static class DiagnosticLogExtensions
{
    public static void Debug(this IDiagnosticLog log, string message) =>
        log.Write(DiagnosticLevel.Debug, message);

    public static void Info(this IDiagnosticLog log, string message) =>
        log.Write(DiagnosticLevel.Information, message);

    public static void Warn(this IDiagnosticLog log, string message, Exception? exception = null) =>
        log.Write(DiagnosticLevel.Warning, message, exception);

    public static void Error(this IDiagnosticLog log, string message, Exception? exception = null) =>
        log.Write(DiagnosticLevel.Error, message, exception);
}
