namespace ChatServidor.Network;

public sealed record ServerClientSnapshot(
    byte Id,
    string Username,
    string IpAddress,
    int Port);

public sealed class ServerClientsChangedEventArgs(IReadOnlyList<ServerClientSnapshot> clients) : EventArgs
{
    public IReadOnlyList<ServerClientSnapshot> Clients { get; } = clients;
}

public sealed class ServerLogEventArgs(string message, DateTimeOffset timestamp) : EventArgs
{
    public string Message { get; } = message;

    public DateTimeOffset Timestamp { get; } = timestamp;
}

public sealed class ServerRunningChangedEventArgs(bool isRunning, int port) : EventArgs
{
    public bool IsRunning { get; } = isRunning;

    public int Port { get; } = port;
}
