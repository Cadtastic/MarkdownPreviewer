using System.Drawing;
using System.Windows.Forms;
using MarkdownPreviewer.Shell.Interop;

namespace MarkdownPreviewer.Shell.Hosting;

/// <summary>
/// A borderless child window that lives inside the HWND the preview host gives us.
/// </summary>
/// <remarks>
/// Why a <see cref="Form"/> and not a <see cref="UserControl"/>: WebView2 needs a
/// real parent window with a message loop, and <c>Form</c> is the only WinForms
/// type that reliably creates a standalone HWND we can then reparent. The trick is
/// to inject <c>WS_CHILD</c> and the parent handle through
/// <see cref="CreateParams"/> <em>before</em> the handle is created, which produces
/// a correctly-styled child in one step instead of creating a top-level popup and
/// converting it afterwards (which flickers and briefly steals activation).
///
/// Consequence: <see cref="AttachTo"/> must be called before anything touches
/// <see cref="Control.Handle"/>.
/// </remarks>
internal sealed class PreviewHostWindow : Form
{
    private IntPtr _parentHandle;

    public PreviewHostWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        Text = string.Empty;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = Padding.Empty;
        Margin = Padding.Empty;

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

            if (_parentHandle != IntPtr.Zero)
            {
                parameters.Style |= NativeMethods.WS_CHILD |
                                    NativeMethods.WS_CLIPCHILDREN |
                                    NativeMethods.WS_CLIPSIBLINGS;
                parameters.Style &= ~NativeMethods.WS_POPUP;
                parameters.ExStyle &= ~NativeMethods.WS_EX_APPWINDOW;
                parameters.ExStyle |= NativeMethods.WS_EX_CONTROLPARENT;
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

        if (IsHandleCreated && _parentHandle != parent)
        {
            // The host moved us — happens when the preview pane is re-docked.
            _parentHandle = parent;
            NativeMethods.SetParent(Handle, parent);
        }
        else
        {
            _parentHandle = parent;
            _ = Handle;   // forces creation with the CreateParams above
        }

        Resize(bounds);
        Visible = true;
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

    /// <summary>
    /// Suppresses activation. A preview pane must never steal focus from the file
    /// list; the user is still arrow-keying through files.
    /// </summary>
    protected override bool ShowWithoutActivation => true;
}
