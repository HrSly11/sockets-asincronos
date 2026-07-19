using System.ComponentModel;
using Guna.UI2.WinForms;

namespace Chat.Presentation;

[DefaultProperty(nameof(IsOnline))]
public sealed class PulseStatusIndicator : UserControl
{
    private readonly Guna2CircleButton statusDot = new();
    private readonly System.Windows.Forms.Timer pulseTimer = new();
    private int pulseFrame;
    private bool isOnline;

    public PulseStatusIndicator()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;

        statusDot.Animated = true;
        statusDot.BorderThickness = 0;
        statusDot.FillColor = Theme.Offline;
        statusDot.DisabledState.BorderColor = Color.Transparent;
        statusDot.DisabledState.FillColor = Theme.Offline;
        statusDot.Enabled = false;
        statusDot.Size = new Size(10, 10);
        statusDot.TabStop = false;

        Controls.Add(statusDot);
        Size = new Size(24, 24);
        MinimumSize = new Size(18, 18);
        statusDot.Location = new Point((Width - statusDot.Width) / 2, (Height - statusDot.Height) / 2);

        pulseTimer.Interval = 35;
        pulseTimer.Tick += HandlePulseTick;
    }

    [DefaultValue(false)]
    public bool IsOnline
    {
        get => isOnline;
        set
        {
            if (isOnline == value)
            {
                return;
            }

            isOnline = value;
            var statusColor = value ? Theme.Success : Theme.Offline;
            statusDot.FillColor = statusColor;
            statusDot.DisabledState.FillColor = statusColor;
            Invalidate();
        }
    }

    public void PulseConnectionChange()
    {
        pulseFrame = 0;
        pulseTimer.Stop();
        pulseTimer.Start();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        statusDot.Location = new Point((Width - statusDot.Width) / 2, (Height - statusDot.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!pulseTimer.Enabled)
        {
            return;
        }

        var progress = pulseFrame / 24F;
        var diameter = statusDot.Width + (int)(14 * progress);
        var alpha = Math.Max(0, 95 - (int)(95 * progress));
        var color = IsOnline ? Theme.Success : Theme.Offline;
        using var pen = new Pen(Color.FromArgb(alpha, color), 2F);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawEllipse(
            pen,
            (Width - diameter) / 2F,
            (Height - diameter) / 2F,
            diameter,
            diameter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pulseTimer.Dispose();
            statusDot.Dispose();
        }

        base.Dispose(disposing);
    }

    private void HandlePulseTick(object? sender, EventArgs e)
    {
        pulseFrame++;
        if (pulseFrame >= 24)
        {
            pulseTimer.Stop();
        }

        Invalidate();
    }
}
