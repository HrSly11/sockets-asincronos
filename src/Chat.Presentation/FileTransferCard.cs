using System.ComponentModel;
using Guna.UI2.WinForms;

namespace Chat.Presentation;

public sealed class FileTransferCard : Guna2Panel
{
    private readonly Label iconLabel;
    private readonly Label fileNameLabel;
    private readonly Label progressLabel;
    private readonly Guna2ProgressBar progressBar;

    public FileTransferCard()
    {
        BorderRadius = Theme.CardRadius;
        BorderThickness = 1;
        BorderColor = Theme.Border;
        FillColor = Theme.Surface;
        Padding = new Padding(16, 12, 16, 12);

        iconLabel = new Label
        {
            AutoSize = false,
            Location = new Point(16, 19),
            Size = new Size(42, 42),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.IconFont(20F),
            ForeColor = Theme.Primary
        };

        fileNameLabel = new Label
        {
            AutoEllipsis = true,
            Location = new Point(66, 13),
            Size = new Size(274, 22),
            Font = Theme.BodyFont(9F, FontStyle.Bold),
            ForeColor = Theme.MainText
        };

        progressLabel = new Label
        {
            AutoEllipsis = true,
            Location = new Point(66, 37),
            Size = new Size(274, 18),
            Font = Theme.MetadataFont(),
            ForeColor = Theme.SecondaryText
        };

        progressBar = new Guna2ProgressBar
        {
            Location = new Point(66, 63),
            Size = new Size(274, 7),
            BorderRadius = Theme.ProgressRadius,
            FillColor = Theme.OtherMessage,
            ProgressColor = Theme.Primary,
            ProgressColor2 = Theme.Primary,
            ShowText = false
        };

        Controls.AddRange([iconLabel, fileNameLabel, progressLabel, progressBar]);
        Size = new Size(360, 92);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string TransferId { get; set; } = string.Empty;

    public void UpdateTransfer(string fileName, int progress, string statusText)
    {
        var safeProgress = Math.Clamp(progress, 0, 100);
        iconLabel.Text = FluentGlyphs.ForFile(fileName);
        fileNameLabel.Text = fileName;
        progressLabel.Text = statusText;
        progressBar.Value = safeProgress;
        progressBar.ProgressColor = safeProgress == 100 ? Theme.Success : Theme.Primary;
        progressBar.ProgressColor2 = progressBar.ProgressColor;
        progressLabel.Invalidate();
        progressBar.Invalidate();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var contentWidth = Math.Max(120, Width - 82);
        fileNameLabel.Width = contentWidth;
        progressLabel.Width = contentWidth;
        progressBar.Width = contentWidth;
    }
}
