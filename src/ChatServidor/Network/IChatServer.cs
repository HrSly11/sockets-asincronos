namespace ChatServidor.Network;

public interface IChatServer : IAsyncDisposable
{
    event EventHandler<ServerClientsChangedEventArgs>? ClientsChanged;

    event EventHandler<ServerLogEventArgs>? LogEmitted;

    event EventHandler<ServerRunningChangedEventArgs>? RunningChanged;

    bool IsRunning { get; }

    int ActualPort { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
