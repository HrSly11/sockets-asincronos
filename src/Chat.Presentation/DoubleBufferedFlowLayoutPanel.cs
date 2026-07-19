namespace Chat.Presentation;

public sealed class DoubleBufferedFlowLayoutPanel : FlowLayoutPanel
{
    public DoubleBufferedFlowLayoutPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}
