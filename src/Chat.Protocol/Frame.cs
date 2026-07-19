namespace Chat.Protocol;

public sealed record Frame(FrameCommand Command, byte RouteId, byte[] Payload)
{
    public const int HeaderLength = 4;
    public const int MaximumPayloadLength = ushort.MaxValue;
}
