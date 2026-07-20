using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Chat.Protocol;

namespace ChatServidor.Network;

public sealed class ChatServer : IChatServer
{
    public const int DefaultPort = 55000;
    private readonly int requestedPort;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly object registryLock = new();
    private readonly Dictionary<byte, ClientConnection> registeredClients = [];
    private readonly HashSet<byte> retiringClientIds = [];
    private readonly ConcurrentDictionary<Guid, ClientConnection> allConnections = [];
    private readonly ConcurrentDictionary<Guid, (string Name, List<byte> MemberIds)> registeredGroups = new();
    private TcpListener? listener;
    private CancellationTokenSource? serverCancellation;
    private Task? acceptLoopTask;
    private bool disposed;

    public ChatServer(int port = DefaultPort)
    {
        if (port is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        requestedPort = port;
    }

    public event EventHandler<ServerClientsChangedEventArgs>? ClientsChanged;

    public event EventHandler<ServerLogEventArgs>? LogEmitted;

    public event EventHandler<ServerRunningChangedEventArgs>? RunningChanged;

    public bool IsRunning { get; private set; }

    public int ActualPort { get; private set; }

    internal Func<byte, CancellationToken, Task>? BeforeRecipientSendAsync { get; set; }

    internal Func<byte, Guid, CancellationToken, Task>? BeforeClientFinallyCleanupAsync
    {
        get;
        set;
    }

    internal Action<byte, Guid, bool>? AfterClientFinallyCleanup { get; set; }

    internal Func<byte, Guid, CancellationToken, Task>? BeforeRetiredClientIdReleaseAsync
    {
        get;
        set;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            var newListener = new TcpListener(IPAddress.Any, requestedPort);
            newListener.Start();
            listener = newListener;
            ActualPort = ((IPEndPoint)newListener.LocalEndpoint).Port;
            serverCancellation = new CancellationTokenSource();
            IsRunning = true;
            acceptLoopTask = AcceptLoopAsync(newListener, serverCancellation.Token);
            EmitLog($"Servidor iniciado en el puerto {ActualPort}.");
            RunningChanged?.Invoke(this, new ServerRunningChangedEventArgs(true, ActualPort));
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning && acceptLoopTask is null)
            {
                return;
            }

            IsRunning = false;
            serverCancellation?.Cancel();
            listener?.Stop();
            foreach (var connection in allConnections.Values)
            {
                connection.Close();
            }

            var acceptTask = acceptLoopTask;
            if (acceptTask is not null)
            {
                await IgnoreExpectedShutdownAsync(acceptTask).ConfigureAwait(false);
            }

            var connectionTasks = allConnections.Values
                .Select(connection => connection.ProcessingTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (connectionTasks.Length > 0)
            {
                await Task.WhenAll(connectionTasks.Select(IgnoreExpectedShutdownAsync)).ConfigureAwait(false);
            }

            lock (registryLock)
            {
                registeredClients.Clear();
                retiringClientIds.Clear();
            }

            allConnections.Clear();
            acceptLoopTask = null;
            listener = null;
            serverCancellation?.Dispose();
            serverCancellation = null;
            EmitClientsChanged([]);
            EmitLog("Servidor detenido.");
            RunningChanged?.Invoke(this, new ServerRunningChangedEventArgs(false, ActualPort));
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        disposed = true;
        lifecycleLock.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcpClient = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                tcpClient.NoDelay = true;
                var connection = new ClientConnection(tcpClient);
                allConnections[connection.ConnectionKey] = connection;
                connection.ProcessingTask = ProcessClientAsync(connection, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            EmitLog($"Error al aceptar clientes: {exception.Message}");
        }
    }

    private async Task ProcessClientAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var registrationFrame = await FrameCodec
                .ReadAsync(connection.Stream, cancellationToken)
                .ConfigureAwait(false);
            if (registrationFrame is null
                || registrationFrame.Command != FrameCommand.Register
                || registrationFrame.RouteId != 0)
            {
                await SendErrorAsync(connection, "El registro inicial no es válido.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            RegisterPayload registration;
            try
            {
                registration = JsonPayload.Deserialize<RegisterPayload>(registrationFrame.Payload);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
            {
                await SendRegistrationResultAsync(
                        connection,
                        new RegistrationResultPayload(false, 0, "El nombre de usuario no es válido."),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var registrationResult = TryRegister(connection, registration.Username);
            await SendRegistrationResultAsync(connection, registrationResult, cancellationToken)
                .ConfigureAwait(false);
            if (!registrationResult.Accepted)
            {
                return;
            }

            EmitLog($"{connection.Username} se conectó con ID {connection.ClientId}.");
            await BroadcastClientListAsync(cancellationToken).ConfigureAwait(false);
            EmitClientsChanged(GetServerSnapshot());

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(connection.Stream, cancellationToken)
                    .ConfigureAwait(false);
                if (frame is null || frame.Command == FrameCommand.Disconnect)
                {
                    break;
                }

                await RouteFrameAsync(connection, frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException
                or SocketException
                or ObjectDisposedException
                or EndOfStreamException)
        {
            if (IsRunning)
            {
                EmitLog($"Conexión interrumpida: {connection.DisplayName}.");
            }
        }
        catch (Exception exception)
        {
            EmitLog($"Error con {connection.DisplayName}: {exception.Message}");
        }
        finally
        {
            if (BeforeClientFinallyCleanupAsync is not null)
            {
                await BeforeClientFinallyCleanupAsync(
                        connection.ClientId,
                        connection.ConnectionKey,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var removed = RetireRegisteredClient(connection);
            AfterClientFinallyCleanup?.Invoke(
                connection.ClientId,
                connection.ConnectionKey,
                removed);
            connection.Close();
            allConnections.TryRemove(connection.ConnectionKey, out _);
            if (removed)
            {
                try
                {
                    if (IsRunning)
                    {
                        EmitLog($"{connection.DisplayName} se desconectó.");
                        await PublishRetirementAsync(connection, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    ReleaseRetiredClientId(connection.ClientId);
                }
            }
        }
    }

    private RegistrationResultPayload TryRegister(ClientConnection connection, string username)
    {
        var cleanName = username.Trim();
        if (!UsernameValidator.IsValid(cleanName))
        {
            return new RegistrationResultPayload(false, 0, "El nombre de usuario no es válido.");
        }

        lock (registryLock)
        {
            if (registeredClients.Values.Any(
                    client => string.Equals(client.Username?.Trim(), cleanName, StringComparison.OrdinalIgnoreCase)))
            {
                return new RegistrationResultPayload(false, 0, ProtocolMessages.DuplicateUsername);
            }

            var id = Enumerable.Range(1, byte.MaxValue)
                .Select(value => (byte)value)
                .FirstOrDefault(candidate =>
                    !registeredClients.ContainsKey(candidate)
                    && !retiringClientIds.Contains(candidate));
            if (id == 0)
            {
                return new RegistrationResultPayload(false, 0, "El servidor alcanzó el límite de clientes.");
            }

            connection.ClientId = id;
            connection.Username = cleanName;
            registeredClients.Add(id, connection);
            return new RegistrationResultPayload(true, id, null);
        }
    }

    private async Task RouteFrameAsync(
        ClientConnection sender,
        Frame frame,
        CancellationToken cancellationToken)
    {
        if (frame.Command == FrameCommand.CreateGroup)
        {
            await HandleCreateGroupAsync(sender, frame, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (frame.Command == FrameCommand.GroupMessage)
        {
            await RouteGroupMessageAsync(sender, frame, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (frame.Command is not (
            FrameCommand.TextMessage
            or FrameCommand.FileStart
            or FrameCommand.FileChunk
            or FrameCommand.FileEnd
            or FrameCommand.FileAbort
            or FrameCommand.EditMessage
            or FrameCommand.DeleteMessage))
        {
            await SendErrorAsync(sender, "El comando recibido no es válido.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (frame.RouteId == 0)
        {
            await SendErrorAsync(sender, ProtocolMessages.MissingTarget, cancellationToken).ConfigureAwait(false);
            return;
        }

        ClientConnection? recipient;
        lock (registryLock)
        {
            registeredClients.TryGetValue(frame.RouteId, out recipient);
        }

        if (recipient is null)
        {
            await SendErrorAsync(sender, ProtocolMessages.MissingTarget, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (BeforeRecipientSendAsync is not null)
        {
            await BeforeRecipientSendAsync(recipient.ClientId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await recipient.SendAsync(
                    new Frame(frame.Command, sender.ClientId, frame.Payload),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is IOException or SocketException or ObjectDisposedException)
        {
            await CleanupFailedRecipientAsync(recipient, cancellationToken).ConfigureAwait(false);
            await SendErrorAsync(sender, ProtocolMessages.MissingTarget, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleCreateGroupAsync(
        ClientConnection sender,
        Frame frame,
        CancellationToken cancellationToken)
    {
        var payload = JsonPayload.Deserialize<CreateGroupPayload>(frame.Payload);
        var cleanName = payload.GroupName.Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            await SendErrorAsync(sender, "El nombre del grupo no puede estar vacío.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var groupId = Guid.NewGuid();
        var memberIds = payload.MemberIds.Concat([sender.ClientId]).Distinct().ToList();

        var members = new List<ClientInfo>();
        var memberConnections = new List<ClientConnection>();
        lock (registryLock)
        {
            foreach (var id in memberIds)
            {
                if (registeredClients.TryGetValue(id, out var conn) && !string.IsNullOrWhiteSpace(conn.Username))
                {
                    members.Add(new ClientInfo(conn.ClientId, conn.Username.Trim()));
                    memberConnections.Add(conn);
                }
            }
        }

        registeredGroups[groupId] = (cleanName, memberIds);
        EmitLog($"Grupo '{cleanName}' creado por {sender.Username} con {members.Count} miembros.");

        var createdPayload = JsonPayload.Serialize(new GroupCreatedPayload(groupId, cleanName, members));
        var responseFrame = new Frame(FrameCommand.GroupCreated, 0, createdPayload);

        foreach (var conn in memberConnections)
        {
            try
            {
                await conn.SendAsync(responseFrame, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task RouteGroupMessageAsync(
        ClientConnection sender,
        Frame frame,
        CancellationToken cancellationToken)
    {
        var payload = JsonPayload.Deserialize<GroupMessagePayload>(frame.Payload);
        if (!registeredGroups.TryGetValue(payload.GroupId, out var group))
        {
            await SendErrorAsync(sender, "El grupo especificado no existe.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var recipients = new List<ClientConnection>();
        lock (registryLock)
        {
            foreach (var id in group.MemberIds)
            {
                if (id != sender.ClientId && registeredClients.TryGetValue(id, out var conn))
                {
                    recipients.Add(conn);
                }
            }
        }

        var routedFrame = new Frame(FrameCommand.GroupMessage, sender.ClientId, frame.Payload);
        foreach (var recipient in recipients)
        {
            try
            {
                await recipient.SendAsync(routedFrame, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task CleanupFailedRecipientAsync(
        ClientConnection recipient,
        CancellationToken cancellationToken)
    {
        var removed = RetireRegisteredClient(recipient);
        recipient.Close();
        if (!removed)
        {
            return;
        }

        try
        {
            if (IsRunning)
            {
                EmitLog($"{recipient.DisplayName} se desconectó durante un envío.");
                await PublishRetirementAsync(recipient, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ReleaseRetiredClientId(recipient.ClientId);
        }
    }

    private async Task PublishRetirementAsync(
        ClientConnection connection,
        CancellationToken cancellationToken)
    {
        await BroadcastClientListAsync(cancellationToken).ConfigureAwait(false);
        EmitClientsChanged(GetServerSnapshot());
        var beforeRelease = BeforeRetiredClientIdReleaseAsync;
        if (beforeRelease is not null)
        {
            await beforeRelease(
                    connection.ClientId,
                    connection.ConnectionKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task BroadcastClientListAsync(CancellationToken cancellationToken)
    {
        ClientConnection[] recipients;
        ClientInfo[] clients;
        lock (registryLock)
        {
            recipients = registeredClients.Values.ToArray();
            clients = recipients
                .Where(client => !string.IsNullOrWhiteSpace(client.Username))
                .OrderBy(client => client.ClientId)
                .Select(client => new ClientInfo(client.ClientId, client.Username!.Trim()))
                .DistinctBy(client => client.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var frame = new Frame(
            FrameCommand.ClientList,
            0,
            JsonPayload.Serialize(new ClientListPayload(clients)));
        await Task.WhenAll(recipients.Select(
                async recipient =>
                {
                    try
                    {
                        await recipient.SendAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is IOException or SocketException or ObjectDisposedException)
                    {
                    }
                }))
            .ConfigureAwait(false);
    }

    private static Task SendRegistrationResultAsync(
        ClientConnection connection,
        RegistrationResultPayload result,
        CancellationToken cancellationToken)
    {
        return connection.SendAsync(
            new Frame(FrameCommand.RegistrationResult, 0, JsonPayload.Serialize(result)),
            cancellationToken);
    }

    private static Task SendErrorAsync(
        ClientConnection connection,
        string message,
        CancellationToken cancellationToken)
    {
        return connection.SendAsync(
            new Frame(FrameCommand.Error, 0, JsonPayload.Serialize(new ErrorPayload(message))),
            cancellationToken);
    }

    private bool RetireRegisteredClient(ClientConnection connection)
    {
        if (connection.ClientId == 0)
        {
            return false;
        }

        lock (registryLock)
        {
            if (!registeredClients.TryGetValue(connection.ClientId, out var registered)
                || !ReferenceEquals(registered, connection))
            {
                return false;
            }

            if (!registeredClients.Remove(connection.ClientId))
            {
                return false;
            }

            retiringClientIds.Add(connection.ClientId);
            return true;
        }
    }

    private void ReleaseRetiredClientId(byte clientId)
    {
        lock (registryLock)
        {
            retiringClientIds.Remove(clientId);
        }
    }

    private IReadOnlyList<ServerClientSnapshot> GetServerSnapshot()
    {
        lock (registryLock)
        {
            return registeredClients.Values
                .OrderBy(client => client.ClientId)
                .Select(client => new ServerClientSnapshot(
                    client.ClientId,
                    client.Username!,
                    client.RemoteEndPoint?.Address.ToString() ?? "Desconocida",
                    client.RemoteEndPoint?.Port ?? 0))
                .ToArray();
        }
    }

    private void EmitClientsChanged(IReadOnlyList<ServerClientSnapshot> clients)
    {
        ClientsChanged?.Invoke(this, new ServerClientsChangedEventArgs(clients));
    }

    private void EmitLog(string message)
    {
        LogEmitted?.Invoke(this, new ServerLogEventArgs(message, DateTimeOffset.Now));
    }

    private static async Task IgnoreExpectedShutdownAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or ObjectDisposedException
                or IOException
                or SocketException)
        {
        }
    }

    private sealed class ClientConnection(TcpClient client)
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private int closed;

        public Guid ConnectionKey { get; } = Guid.NewGuid();

        public TcpClient Client { get; } = client;

        public NetworkStream Stream { get; } = client.GetStream();

        public IPEndPoint? RemoteEndPoint { get; } = client.Client.RemoteEndPoint as IPEndPoint;

        public byte ClientId { get; set; }

        public string? Username { get; set; }

        public string DisplayName => Username ?? RemoteEndPoint?.ToString() ?? "cliente";

        public Task? ProcessingTask { get; set; }

        public async Task SendAsync(Frame frame, CancellationToken cancellationToken)
        {
            await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await FrameCodec.WriteAsync(Stream, frame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0)
            {
                return;
            }

            Client.Close();
        }
    }
}
