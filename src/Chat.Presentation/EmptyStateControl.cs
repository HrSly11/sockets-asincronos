using System.ComponentModel;

namespace Chat.Presentation;

public sealed class EmptyStateControl : UserControl
{
    private readonly Label messageLabel;
    private readonly Label glyphLabel;

    public EmptyStateControl()
    {
        BackColor = Color.Transparent;
        AutoSize = false;
        Height = 96;
        Dock = DockStyle.Top;

        glyphLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 38,
            Text = FluentGlyphs.Chat,
            TextAlign = ContentAlignment.BottomCenter,
            Font = Theme.IconFont(16F),
            ForeColor = Theme.SecondaryText
        };

        messageLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            Font = Theme.BodyFont(),
            ForeColor = Theme.SecondaryText,
            AutoEllipsis = true,
            Padding = new Padding(8, 8, 8, 0)
        };

        Controls.Add(messageLabel);
        Controls.Add(glyphLabel);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Message
    {
        get => messageLabel.Text;
        set => messageLabel.Text = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph
    {
        get => glyphLabel.Text;
        set => glyphLabel.Text = value;
    }
}
