namespace ChatServidor;

public enum ServerPresentationState
{
    Stopped,
    Starting,
    Listening,
    Error
}

public sealed record ConnectedClientView(
    string Id,
    string UserName,
    string IpAddress,
    int Port,
    string Status);

public sealed class ServerStateChangeRequestedEventArgs(bool shouldStart, int port) : EventArgs
{
    public bool ShouldStart { get; } = shouldStart;

    public int Port { get; } = port;
}
