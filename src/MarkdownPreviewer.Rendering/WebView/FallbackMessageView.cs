using System.Drawing;
using System.Windows.Forms;

namespace MarkdownPreviewer.Rendering.WebView;

/// <summary>
/// Plain-text surface shown when WebView2 cannot be used at all.
/// </summary>
/// <remarks>
/// The default failure mode of a broken preview handler is a blank white
/// rectangle, which tells the user nothing and tells you nothing either. This
/// control exists so that "WebView2 runtime is missing" or "assets did not
/// install" reads as a sentence in the preview pane instead of as silence.
/// </remarks>
internal sealed class FallbackMessageView : Label
{
    public FallbackMessageView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(16);
        TextAlign = ContentAlignment.TopLeft;
        AutoSize = false;
        UseMnemonic = false;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
    }

    public void Show(string heading, string detail)
    {
        Text = string.Concat(heading, Environment.NewLine, Environment.NewLine, detail);
        Visible = true;
        BringToFront();
    }
}
