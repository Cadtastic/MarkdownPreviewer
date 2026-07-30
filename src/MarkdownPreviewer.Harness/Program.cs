using System.Windows.Forms;

namespace MarkdownPreviewer.Harness;

/// <summary>
/// Standalone host for the preview surface.
/// </summary>
/// <remarks>
/// This exists because the alternative development loop is: build, copy to the
/// install directory, kill every <c>explorer.exe</c> and <c>prevhost.exe</c>,
/// reopen Explorer, click a file, and read a log file to find out what broke. That
/// loop is measured in minutes and cannot be attached to a debugger before the
/// interesting code has already run.
///
/// The harness drives the identical object graph — same
/// <c>WebView2PreviewSurface</c>, same <c>PreviewSession</c>, same asset tree — so
/// anything that renders correctly here renders correctly in Explorer. Only the
/// COM shim is absent.
/// </remarks>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new HarnessWindow(args.Length > 0 ? args[0] : null));
    }
}
