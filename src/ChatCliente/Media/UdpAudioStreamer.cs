using System.Net;
using System.Net.Sockets;

namespace ChatCliente.Media;

public sealed class UdpAudioStreamer : IAsyncDisposable
{
    private readonly UdpClient udpClient;
    private CancellationTokenSource? cts;
    private Task? listenTask;
    private bool isStreaming;

    public int LocalPort => ((IPEndPoint)udpClient.Client.LocalEndPoint!).Port;

    public event EventHandler<byte[]>? AudioChunkReceived;

    public UdpAudioStreamer()
    {
        udpClient = new UdpClient(0); // Bind to any free UDP port
    }

    public void StartStreaming(string serverHost, int serverUdpPort, byte senderId, byte targetId)
    {
        if (isStreaming) return;
        isStreaming = true;
        cts = new CancellationTokenSource();
        var token = cts.Token;

        listenTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync(token).ConfigureAwait(false);
                    if (result.Buffer.Length > 2)
                    {
                        // Payload format: [SenderId (1 byte), TargetId (1 byte), PCM bytes...]
                        var pcmData = result.Buffer[2..];
                        AudioChunkReceived?.Invoke(this, pcmData);
                    }
                }
                catch
                {
                    break;
                }
            }
        }, token);

        // Background simulation for PCM audio packets (e.g. 20ms chunks) sent to server
        _ = Task.Run(async () =>
        {
            var serverEndpoint = new IPEndPoint(IPAddress.Parse(serverHost == "localhost" ? "127.0.0.1" : serverHost), serverUdpPort);
            var dummyPcm = new byte[640]; // 20ms of 16kHz 16-bit mono PCM
            var packet = new byte[2 + dummyPcm.Length];
            packet[0] = senderId;
            packet[1] = targetId;

            while (isStreaming && !token.IsCancellationRequested)
            {
                try
                {
                    await udpClient.SendAsync(packet, packet.Length, serverEndpoint).ConfigureAwait(false);
                    await Task.Delay(20, token).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }, token);
    }

    public async ValueTask DisposeAsync()
    {
        isStreaming = false;
        cts?.Cancel();
        udpClient.Close();
        if (listenTask is not null)
        {
            try { await listenTask.ConfigureAwait(false); } catch { }
        }
        cts?.Dispose();
    }
}
