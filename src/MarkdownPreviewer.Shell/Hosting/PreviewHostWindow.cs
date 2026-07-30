using System.Drawing;
using System.Windows.Forms;
using MarkdownPreviewer.Application.Abstractions;
using MarkdownPreviewer.Shell.Interop;

namespace MarkdownPreviewer.Shell.Hosting;

/// <summary>
/// A borderless child window that lives inside the HWND the preview host gives us.
/// </summary>
/// <remarks>
/// <para>Why a <see cref="Control"/> and not a <see cref="Form"/>: a control IS a
/// child window. Its <see cref="Control.Visible"/> maps directly onto
/// <c>WS_VISIBLE</c> and its bounds onto <c>SetWindowPos</c>, with none of a
/// form's top-level machinery in between. An earlier revision used a
/// <see cref="Form"/> with <c>WS_CHILD</c> injected through
/// <see cref="CreateParams"/>; the form's visibility pipeline — which still
/// believed it was top-level — never applied <c>WS_VISIBLE</c> and offset the
/// bounds by phantom non-client margins, which showed up as a rendered-but-blank
/// preview pane.</para>
///
/// <para>The parent is a raw HWND owned by the preview host (prevhost.exe), not a
/// WinForms control, so it is injected through <see cref="CreateParams.Parent"/>
/// before the handle is created rather than via <see cref="Control.Parent"/>.
/// Consequence: <see cref="AttachTo"/> must be called before anything touches
/// <see cref="Control.Handle"/>.</para>
/// </remarks>
internal sealed class PreviewHostWindow : Control
{
    private readonly IDiagnosticLog _log;
    private IntPtr _parentHandle;

    public PreviewHostWindow(IDiagnosticLog log)
    {
        _log = log;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ContainerControl,
            true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;

            parameters.Style |= NativeMethods.WS_CHILD |
                                NativeMethods.WS_CLIPCHILDREN |
                                NativeMethods.WS_CLIPSIBLINGS;
            parameters.ExStyle |= NativeMethods.WS_EX_CONTROLPARENT;

            if (_parentHandle != IntPtr.Zero)
            {
                parameters.Parent = _parentHandle;
            }

            return parameters;
        }
    }

    /// <summary>
    /// Parents this window inside <paramref name="parent"/> and sizes it to
    /// <paramref name="bounds"/> (parent client coordinates).
    /// </summary>
    public void AttachTo(IntPtr parent, Rectangle bounds)
    {
        if (parent == IntPtr.Zero || !NativeMethods.IsWindow(parent))
        {
            throw new ArgumentException("The preview host supplied an invalid window handle.", nameof(parent));
        }

        if (_log.IsEnabled(DiagnosticLevel.Debug))
        {
            NativeMethods.GetWindowThreadProcessId(parent, out uint parentPid);
            NativeMethods.GetWindowRect(parent, out RECT parentRect);
            _log.Debug(
                $"AttachTo: parent=0x{parent:X} (pid {parentPid}, " +
                $"{parentRect.Width}x{parentRect.Height}, " +
                $"visible={NativeMethods.IsWindowVisible(parent)}), " +
                $"requested bounds={bounds}, created={IsHandleCreated}.");
        }

        if (IsHandleCreated && _parentHandle != parent)
        {
            // The host moved us — happens when the preview pane is re-docked.
            _parentHandle = parent;
            NativeMethods.SetParent(Handle, parent);
            _log.Debug($"AttachTo: reparented existing window 0x{Handle:X}.");
        }
        else
        {
            _parentHandle = parent;
            _ = Handle;   // forces creation with the CreateParams above
        }

        Resize(bounds);
        Visible = true;

        if (_log.IsEnabled(DiagnosticLevel.Debug))
        {
            _log.Debug(
                $"AttachTo: window=0x{Handle:X}, bounds={Bounds}, " +
                $"visible={NativeMethods.IsWindowVisible(Handle)}.");
        }
    }

    /// <summary>Repositions the window, for <c>IPreviewHandler.SetRect</c>.</summary>
    /// <remarks>
    /// Intentionally shadows the inherited <see cref="Control.Resize"/> event —
    /// hence <c>new</c>. The name mirrors the COM method it services.
    /// </remarks>
    public new void Resize(Rectangle bounds)
    {
        // A collapsed pane reports a zero or negative rect; clamp rather than
        // letting Win32 reject the call.
        Bounds = new Rectangle(
            bounds.X,
            bounds.Y,
            Math.Max(bounds.Width, 0),
            Math.Max(bounds.Height, 0));
    }
}
