using System.Buffers.Binary;
using Chat.Protocol;
using Xunit;

namespace Chat.FunctionalTests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task FrameCodec_round_trips_and_writes_big_endian_length()
    {
        var payload = Enumerable.Range(0, 258).Select(value => (byte)value).ToArray();
        var frame = new Frame(FrameCommand.TextMessage, 42, payload);
        await using var stream = new MemoryStream();

        await FrameCodec.WriteAsync(stream, frame);

        var bytes = stream.ToArray();
        Assert.Equal((byte)FrameCommand.TextMessage, bytes[0]);
        Assert.Equal(42, bytes[1]);
        Assert.Equal(payload.Length, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2)));
        stream.Position = 0;
        var decoded = await FrameCodec.ReadAsync(stream);
        Assert.NotNull(decoded);
        Assert.Equal(frame.Command, decoded.Command);
        Assert.Equal(frame.RouteId, decoded.RouteId);
        Assert.Equal(payload, decoded.Payload);
    }

    [Fact]
    public async Task FrameCodec_reads_a_frame_from_one_byte_fragments()
    {
        var expected = new Frame(FrameCommand.FileChunk, 7, Enumerable.Range(0, 80).Select(i => (byte)i).ToArray());
        await using var encoded = new MemoryStream();
        await FrameCodec.WriteAsync(encoded, expected);
        await using var fragmented = new FragmentedReadStream(encoded.ToArray(), 1);

        var actual = await FrameCodec.ReadAsync(fragmented);

        Assert.NotNull(actual);
        Assert.Equal(expected.Command, actual.Command);
        Assert.Equal(expected.RouteId, actual.RouteId);
        Assert.Equal(expected.Payload, actual.Payload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task FrameCodec_distinguishes_clean_EOF_from_truncated_frames(int availableBytes)
    {
        var bytes = new byte[] { (byte)FrameCommand.TextMessage, 1, 0, 2, 65, 66 };
        await using var stream = new MemoryStream(bytes.AsSpan(0, availableBytes).ToArray());

        if (availableBytes == 0)
        {
            Assert.Null(await FrameCodec.ReadAsync(stream));
        }
        else
        {
            await Assert.ThrowsAsync<EndOfStreamException>(
                async () => await FrameCodec.ReadAsync(stream));
        }
    }

    [Fact]
    public async Task FrameCodec_supports_the_maximum_payload()
    {
        var payload = new byte[ushort.MaxValue];
        Random.Shared.NextBytes(payload);
        var frame = new Frame(FrameCommand.FileChunk, byte.MaxValue, payload);
        await using var stream = new MemoryStream();

        await FrameCodec.WriteAsync(stream, frame);
        stream.Position = 0;
        var decoded = await FrameCodec.ReadAsync(stream);

        Assert.NotNull(decoded);
        Assert.Equal(ushort.MaxValue, decoded.Payload.Length);
        Assert.Equal(payload, decoded.Payload);
    }

    [Fact]
    public void Protocol_defines_the_required_command_numbers_and_payload_roundtrips()
    {
        Assert.Equal(1, (byte)FrameCommand.TextMessage);
        Assert.Equal(2, (byte)FrameCommand.FileChunk);
        Assert.Equal(3, (byte)FrameCommand.FileEnd);
        Assert.Equal(4, (byte)FrameCommand.ClientList);
        Assert.Equal(5, (byte)FrameCommand.Disconnect);
        Assert.Equal(6, (byte)FrameCommand.Register);
        Assert.Equal(7, (byte)FrameCommand.RegistrationResult);
        Assert.Equal(8, (byte)FrameCommand.FileStart);
        Assert.Equal(9, (byte)FrameCommand.Error);
        Assert.Equal(10, (byte)FrameCommand.FileAbort);

        var payload = new ClientListPayload([new ClientInfo(5, "Ada"), new ClientInfo(9, "Grace")]);
        var encoded = JsonPayload.Serialize(payload);
        var decoded = JsonPayload.Deserialize<ClientListPayload>(encoded);
        Assert.Equal(payload.Clients, decoded.Clients);

        var transferId = Guid.NewGuid();
        var abortEncoded = JsonPayload.Serialize(new FileAbortPayload(transferId));
        var abortDecoded = JsonPayload.Deserialize<FileAbortPayload>(abortEncoded);
        Assert.Equal(transferId, abortDecoded.TransferId);
    }

    [Theory]
    [InlineData("Ada", true)]
    [InlineData("Grace Hopper", true)]
    [InlineData("dev_01-test", true)]
    [InlineData("", false)]
    [InlineData("bad/name", false)]
    [InlineData("123456789012345678901", false)]
    public void Username_validation_matches_the_wire_contract(string username, bool expected)
    {
        Assert.Equal(expected, UsernameValidator.IsValid(username));
    }

    private sealed class FragmentedReadStream(byte[] bytes, int fragmentSize) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position == bytes.Length)
            {
                return ValueTask.FromResult(0);
            }

            var count = Math.Min(fragmentSize, Math.Min(buffer.Length, bytes.Length - position));
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return ValueTask.FromResult(count);
        }
    }
}
