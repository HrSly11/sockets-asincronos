using ChatServidor.Network;

namespace ChatServidor.Application;

public sealed class ServerCoordinator : IAsyncDisposable
{
    private readonly ServerMonitorForm form;
    private readonly Func<int, IChatServer> serverFactory;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private bool closing;
    private bool disposed;

    public ServerCoordinator(
        ServerMonitorForm form,
        Func<int, IChatServer>? serverFactory = null)
    {
        this.form = form ?? throw new ArgumentNullException(nameof(form));
        this.serverFactory = serverFactory ?? (port => new ChatServer(port));
        form.ServerStateChangeRequested += HandleStateChangeRequested;
        form.FormClosing += HandleFormClosing;
        IsWired = true;
    }

    public bool IsWired { get; }

    public IChatServer? Server { get; private set; }

    public int ActualPort => Server?.ActualPort ?? 0;

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Server?.IsRunning == true)
            {
                return;
            }

            form.SetServerState(ServerPresentationState.Starting, Math.Max(1, port));
            IChatServer? server = null;
            try
            {
                server = serverFactory(port);
                Server = server;
                WireServer(server);
                await server.StartAsync(cancellationToken).ConfigureAwait(false);
                form.SetServerState(ServerPresentationState.Listening, server.ActualPort);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (server is not null)
                {
                    await ReleaseServerAsync(server).ConfigureAwait(false);
                }

                throw;
            }
            catch (Exception exception)
            {
                ReportFailure("iniciar", port, exception);
                if (server is not null)
                {
                    await ReleaseServerAsync(server).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var server = Server;
            var stoppedCleanly = true;
            if (server is not null)
            {
                try
                {
                    await server.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    stoppedCleanly = false;
                    ReportFailure("detener", server.ActualPort, exception);
                }
                finally
                {
                    await ReleaseServerAsync(server).ConfigureAwait(false);
                }
            }

            form.UpdateClients([]);
            if (stoppedCleanly)
            {
                form.SetServerState(
                    ServerPresentationState.Stopped,
                    Math.Max(1, form.SelectedPort));
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        closing = true;
        form.ServerStateChangeRequested -= HandleStateChangeRequested;
        form.FormClosing -= HandleFormClosing;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportFailure("cerrar", ActualPort, exception);
        }

        disposed = true;
        operationLock.Dispose();
    }

    private async void HandleStateChangeRequested(
        object? sender,
        ServerStateChangeRequestedEventArgs args)
    {
        try
        {
            if (args.ShouldStart)
            {
                await StartAsync(args.Port);
            }
            else
            {
                await StopAsync();
            }
        }
        catch (OperationCanceledException) when (closing || disposed)
        {
        }
        catch (Exception exception)
        {
            ReportFailure(args.ShouldStart ? "iniciar" : "detener", args.Port, exception);
        }
    }

    private async void HandleFormClosing(object? sender, FormClosingEventArgs args)
    {
        closing = true;
        try
        {
            await StopAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("cerrar", ActualPort, exception);
        }
    }

    private void HandleClientsChanged(object? sender, ServerClientsChangedEventArgs args)
    {
        try
        {
            form.UpdateClients(args.Clients.Select(client => new ConnectedClientView(
                client.Id.ToString(),
                client.Username,
                client.IpAddress,
                client.Port,
                "Conectado")));
        }
        catch (Exception exception)
        {
            ReportFailure("actualizar clientes", ActualPort, exception);
        }
    }

    private void HandleLogEmitted(object? sender, ServerLogEventArgs args)
    {
        try
        {
            form.AppendLog(args.Message, args.Timestamp);
        }
        catch (Exception exception)
        {
            ReportFailure("actualizar el registro", ActualPort, exception);
        }
    }

    private void WireServer(IChatServer server)
    {
        server.ClientsChanged += HandleClientsChanged;
        server.LogEmitted += HandleLogEmitted;
    }

    private void UnwireServer(IChatServer server)
    {
        server.ClientsChanged -= HandleClientsChanged;
        server.LogEmitted -= HandleLogEmitted;
    }

    private async Task ReleaseServerAsync(IChatServer server)
    {
        UnwireServer(server);
        if (ReferenceEquals(Server, server))
        {
            Server = null;
        }

        try
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportFailure("liberar", server.ActualPort, exception);
        }
    }

    private void ReportFailure(string action, int port, Exception exception)
    {
        var safePort = Math.Clamp(port, 1, ushort.MaxValue);
        form.SetServerState(
            ServerPresentationState.Error,
            safePort,
            exception.Message);
        form.AppendLog($"No se pudo {action} el servidor: {exception.Message}");
    }
}
