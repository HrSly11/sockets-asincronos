using System.Net.Sockets;
using Chat.Protocol;
using ChatCliente.Media;
using ChatCliente.Network;
using ChatServidor.Network;
using Xunit;

namespace Chat.FunctionalTests;

public sealed class VoiceMessagingAndCallsTests
{
    [Fact]
    public async Task VoiceNote_SentOverTcp_IsDeliveredToRecipient()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();

        var tempDir = Path.GetTempPath();
        await using var clientA = new ChatClient(tempDir);
        await using var clientB = new ChatClient(tempDir);

        await clientA.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "UserA");
        await clientB.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "UserB");

        await Task.Delay(200);

        var voiceNoteTcs = new TaskCompletionSource<VoiceNoteReceivedEventArgs>();
        clientB.VoiceNoteReceived += (_, e) => voiceNoteTcs.TrySetResult(e);

        var vnId = Guid.NewGuid().ToString("N");
        var audioBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await clientA.SendVoiceNoteAsync(clientB.ClientId, vnId, 3000, "audio.wav", audioBytes);

        var receivedNote = await voiceNoteTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(clientA.ClientId, receivedNote.SenderId);
        Assert.Equal(vnId, receivedNote.VoiceNoteId);
        Assert.Equal(3000, receivedNote.DurationMs);
        Assert.Equal(audioBytes, receivedNote.AudioData);
    }

    [Fact]
    public async Task VoiceCall_SignalingAndUdpRelay_AndConcurrentTextMessageingWorks()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();

        var tempDir = Path.GetTempPath();
        await using var clientA = new ChatClient(tempDir);
        await using var clientB = new ChatClient(tempDir);

        await clientA.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "UserA");
        await clientB.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "UserB");

        await Task.Delay(200);

        var offerTcs = new TaskCompletionSource<CallOfferEventArgs>();
        clientB.CallOffered += (_, e) => offerTcs.TrySetResult(e);

        var callId = Guid.NewGuid();
        await clientA.SendCallOfferAsync(clientB.ClientId, callId, "UserA", 55001);

        var offer = await offerTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(clientA.ClientId, offer.CallerId);
        Assert.Equal(callId, offer.CallId);

        var answerTcs = new TaskCompletionSource<CallAnswerEventArgs>();
        clientA.CallAnswered += (_, e) => answerTcs.TrySetResult(e);

        await clientB.SendCallAnswerAsync(clientA.ClientId, callId, true, null, 55002);
        var answer = await answerTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(answer.Accepted);

        // Start UDP audio streaming between A and B via server's UDP port
        var serverUdpPort = server.ActualUdpPort;
        await using var streamerA = new UdpAudioStreamer();
        await using var streamerB = new UdpAudioStreamer();

        var udpReceivedB = new TaskCompletionSource<byte[]>();
        streamerB.AudioChunkReceived += (_, pcm) => udpReceivedB.TrySetResult(pcm);

        // Both clients start streaming to register UDP endpoints on server
        streamerB.StartStreaming("127.0.0.1", serverUdpPort, clientB.ClientId, clientA.ClientId);
        streamerA.StartStreaming("127.0.0.1", serverUdpPort, clientA.ClientId, clientB.ClientId);

        // Verify UDP audio packets are received
        var audioPcm = await udpReceivedB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(audioPcm);
        Assert.Equal(640, audioPcm.Length);

        // Concurrent test: Send text message over TCP during active UDP voice call
        var textTcs = new TaskCompletionSource<TextMessageReceivedEventArgs>();
        clientB.MessageReceived += (_, e) => textTcs.TrySetResult(e);

        await clientA.SendMessageAsync(clientB.ClientId, "Mensaje de texto durante llamada activa!");
        var receivedText = await textTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("Mensaje de texto durante llamada activa!", receivedText.Text);
    }
}
