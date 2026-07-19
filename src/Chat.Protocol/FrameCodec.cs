using System.Buffers.Binary;

namespace Chat.Protocol;

public static class FrameCodec
{
    public static async ValueTask<Frame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[Frame.HeaderLength];
        var headerBytesRead = await ReadAtMostAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerBytesRead == 0)
        {
            return null;
        }

        if (headerBytesRead != header.Length)
        {
            throw new EndOfStreamException("The stream ended in the middle of a frame header.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            var payloadBytesRead = await ReadAtMostAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            if (payloadBytesRead != payload.Length)
            {
                throw new EndOfStreamException("The stream ended in the middle of a frame payload.");
            }
        }

        return new Frame((FrameCommand)header[0], header[1], payload);
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        Frame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Payload);
        if (frame.Payload.Length > Frame.MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                $"A frame payload cannot exceed {Frame.MaximumPayloadLength} bytes.");
        }

        var header = new byte[Frame.HeaderLength];
        header[0] = (byte)frame.Command;
        header[1] = frame.RouteId;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)frame.Payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (frame.Payload.Length > 0)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
