using System.Net.Sockets;
using System.Text;
using Chat.Protocol;

namespace ChatCliente.Network;

public sealed class ChatClient : IChatClient
{
    private static readonly HashSet<string> WindowsDeviceNames = new(
        new[]
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "COM¹",
            "COM²",
            "COM³",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
            "LPT¹",
            "LPT²",
            "LPT³"
        },
        StringComparer.OrdinalIgnoreCase);

    private readonly string downloadsDirectory;
    private readonly IFileConflictResolver? fileConflictResolver;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly Dictionary<(byte SenderId, Guid TransferId), IncomingTransfer> incomingTransfers = [];
    private readonly Dictionary<(byte SenderId, Guid TransferId), FinalizationOperation>
        finalizations = [];
    private readonly object finalizationsLock = new();
    private readonly object connectionEventLock = new();
    private readonly Queue<ConnectionEventWorkItem> connectionEvents = [];
    private TcpClient? tcpClient;
    private NetworkStream? stream;
    private CancellationTokenSource? receiveCancellation;
    private Task? receiveLoopTask;
    private long nextConnectionGeneration;
    private long activeConnectionGeneration;
    private long disconnectedConnectionGeneration;
    private bool dispatchingConnectionEvents;
    private bool disposed;
    private IReadOnlyList<ClientInfo> clients = [];

    public ChatClient(
        string downloadsDirectory,
        IFileConflictResolver? fileConflictResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsDirectory);
        this.downloadsDirectory = Path.GetFullPath(downloadsDirectory);
        this.fileConflictResolver = fileConflictResolver;
    }

    internal Func<Guid, int, CancellationToken, Task>? BeforeFileChunkSendAsync { get; set; }

    internal Func<Guid, int, CancellationToken, Task>? AfterFileChunkSendBeforeProgressAsync
    {
        get;
        set;
    }

    internal Func<Guid, CancellationToken, Task<bool>>?
        TrySendFileAbortAsyncOverride
    { get; set; }

    internal Func<Guid, CancellationToken, Task>?
        BeforeIncomingFileCommitAsync
    { get; set; }

    internal Func<long, CancellationToken, Task>? BeforeReceiveLoopCleanupAsync { get; set; }

    internal Func<long, CancellationToken, Task>? BeforeAcceptedSessionPublishAsync { get; set; }

    public event EventHandler<ClientListChangedEventArgs>? ClientListChanged;

    public event EventHandler<TextMessageReceivedEventArgs>? MessageReceived;

    public event EventHandler<FileProgressEventArgs>? FileProgressChanged;

    public event EventHandler<FileReceivedEventArgs>? FileReceived;

    public event EventHandler<ClientErrorEventArgs>? ErrorReceived;

    public event EventHandler? Disconnected;

    public event EventHandler<MessageEditedEventArgs>? MessageEdited;

    public event EventHandler<MessageDeletedEventArgs>? MessageDeleted;

    public byte ClientId { get; private set; }

    public bool IsConnected { get; private set; }

    public IReadOnlyList<ClientInfo> Clients
    {
        get
        {
            lock (connectionEventLock)
            {
                return clients;
            }
        }
    }

    public async Task<RegistrationResultPayload> ConnectAndRegisterAsync(
        string host,
        int port,
        string username,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                throw new InvalidOperationException("The client is already connected.");
            }

            await AwaitAndClearReceiveLoopAsync().ConfigureAwait(false);
            if (tcpClient is not null)
            {
                throw new InvalidOperationException("The client is already connected.");
            }

            var newClient = new TcpClient { NoDelay = true };
            tcpClient = newClient;
            try
            {
                await newClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                var sessionStream = newClient.GetStream();
                stream = sessionStream;
                await SendFrameAsync(
                        new Frame(
                            FrameCommand.Register,
                            0,
                            JsonPayload.Serialize(new RegisterPayload(username))),
                        cancellationToken)
                    .ConfigureAwait(false);

                var response = await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (response is null || response.Command != FrameCommand.RegistrationResult)
                {
                    throw new InvalidDataException("The server did not return a registration result.");
                }

                var result = JsonPayload.Deserialize<RegistrationResultPayload>(response.Payload);
                if (!result.Accepted)
                {
                    CloseTransport();
                    return result;
                }

                var generation = Interlocked.Increment(ref nextConnectionGeneration);
                var sessionCancellation = new CancellationTokenSource();
                var startGate = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var sessionTask = ReceiveLoopAsync(
                    generation,
                    sessionStream,
                    sessionCancellation,
                    startGate.Task);
                try
                {
                    var beforePublish = BeforeAcceptedSessionPublishAsync;
                    if (beforePublish is not null)
                    {
                        await beforePublish(generation, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    lock (connectionEventLock)
                    {
                        ClientId = result.ClientId;
                        IsConnected = true;
                        activeConnectionGeneration = generation;
                        disconnectedConnectionGeneration = 0;
                        receiveCancellation = sessionCancellation;
                        receiveLoopTask = sessionTask;
                    }

                    startGate.TrySetResult();
                }
                catch
                {
                    sessionCancellation.Cancel();
                    startGate.TrySetCanceled();
                    CloseTransport();
                    try
                    {
                        await IgnoreExpectedShutdownAsync(sessionTask).ConfigureAwait(false);
                    }
                    catch (Exception cleanupException)
                    {
                        InvokeSubscribersSafely(
                            ErrorReceived,
                            new ClientErrorEventArgs(cleanupException.Message));
                    }
                    finally
                    {
                        sessionCancellation.Dispose();
                    }

                    throw;
                }

                return result;
            }
            catch
            {
                CloseTransport();
                throw;
            }
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public Task SendMessageAsync(
        byte targetId,
        string text,
        CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(targetId, Guid.NewGuid().ToString("N"), text, cancellationToken);
    }

    public Task SendMessageAsync(
        byte targetId,
        string messageId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (targetId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("The message cannot be empty.", nameof(text));
        }

        var payload = JsonPayload.Serialize(new TextMessagePayload(messageId, text));
        var connectionGeneration = GetConnectedGeneration();
        return SendFrameAsync(
            new Frame(FrameCommand.TextMessage, targetId, payload),
            cancellationToken,
            connectionGeneration);
    }

    public Task SendEditMessageAsync(
        byte targetId,
        string messageId,
        string newText,
        CancellationToken cancellationToken = default)
    {
        if (targetId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newText);
        var connectionGeneration = GetConnectedGeneration();
        return SendFrameAsync(
            new Frame(
                FrameCommand.EditMessage,
                targetId,
                JsonPayload.Serialize(new EditMessagePayload(messageId, newText))),
            cancellationToken,
            connectionGeneration);
    }

    public Task SendDeleteMessageAsync(
        byte targetId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        if (targetId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var connectionGeneration = GetConnectedGeneration();
        return SendFrameAsync(
            new Frame(
                FrameCommand.DeleteMessage,
                targetId,
                JsonPayload.Serialize(new DeleteMessagePayload(messageId))),
            cancellationToken,
            connectionGeneration);
    }

    public async Task<Guid> SendFileAsync(
        byte targetId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (targetId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var connectionGeneration = GetConnectedGeneration();
        var sourcePath = Path.GetFullPath(filePath);
        var fileInfo = new FileInfo(sourcePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The selected file does not exist.", sourcePath);
        }

        var buffer = new byte[FileChunkPayload.MaximumChunkLength];
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var transferId = Guid.NewGuid();
        var displayName = Path.GetFileName(sourcePath);
        var transferStarted = false;
        try
        {
            var startPayload = new FileStartPayload(transferId, displayName, source.Length);
            await SendFrameAsync(
                    new Frame(FrameCommand.FileStart, targetId, JsonPayload.Serialize(startPayload)),
                    cancellationToken,
                    connectionGeneration)
                .ConfigureAwait(false);
            transferStarted = true;

            long transferred = 0;
            var chunkIndex = 0;
            var lastReportedPercentage = -1;
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var beforeChunkSend = BeforeFileChunkSendAsync;
                if (beforeChunkSend is not null)
                {
                    await beforeChunkSend(transferId, chunkIndex, cancellationToken)
                        .ConfigureAwait(false);
                }

                await SendFrameAsync(
                        new Frame(
                            FrameCommand.FileChunk,
                            targetId,
                            FileChunkPayload.Create(transferId, buffer.AsSpan(0, count))),
                        cancellationToken,
                        connectionGeneration)
                    .ConfigureAwait(false);
                var afterChunkSend = AfterFileChunkSendBeforeProgressAsync;
                if (afterChunkSend is not null)
                {
                    await afterChunkSend(transferId, chunkIndex, cancellationToken)
                        .ConfigureAwait(false);
                }

                transferred += count;
                var currentPercentage = CalculatePercentage(transferred, source.Length);
                if (currentPercentage != lastReportedPercentage || transferred == source.Length)
                {
                    lastReportedPercentage = currentPercentage;
                    await EmitFileProgressAsync(
                            connectionGeneration,
                            transferId,
                            targetId,
                            displayName,
                            currentPercentage,
                            true)
                        .ConfigureAwait(false);
                }

                chunkIndex++;
                if (chunkIndex % 8 == 0)
                {
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Yield();
                }
            }

            await SendFrameAsync(
                    new Frame(
                        FrameCommand.FileEnd,
                        targetId,
                        JsonPayload.Serialize(new FileEndPayload(transferId))),
                    cancellationToken,
                    connectionGeneration)
                .ConfigureAwait(false);
            transferStarted = false;
            await EmitFileProgressAsync(
                    connectionGeneration,
                    transferId,
                    targetId,
                    displayName,
                    100,
                    true)
                .ConfigureAwait(false);
        }
        catch
        {
            if (transferStarted)
            {
                var abortSent = await TrySendFileAbortAsync(
                        targetId,
                        transferId,
                        connectionGeneration)
                    .ConfigureAwait(false);
                if (!abortSent)
                {
                    AbortConnectionGeneration(connectionGeneration);
                }
            }

            throw;
        }

        return transferId;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connectionGeneration = GetCurrentConnectionGeneration();
            var hadRegisteredSession = connectionGeneration != 0
                && (IsConnected || ClientId != 0);
            if (IsConnected)
            {
                try
                {
                    await SendFrameAsync(
                            new Frame(FrameCommand.Disconnect, 0, []),
                            cancellationToken,
                            connectionGeneration)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or SocketException or ObjectDisposedException)
                {
                }
            }

            receiveCancellation?.Cancel();
            CloseTransport(connectionGeneration);
            var receiveTask = receiveLoopTask;
            if (receiveTask is not null && Task.CurrentId != receiveTask.Id)
            {
                await AwaitAndClearReceiveLoopAsync().ConfigureAwait(false);
            }
            else if (receiveTask is null)
            {
                CleanupIncomingTransfers();
                CancelFinalizations();
                await DrainFinalizationsAsync().ConfigureAwait(false);
                if (hadRegisteredSession)
                {
                    await RaiseDisconnectedAsync(connectionGeneration).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public void AbortConnection()
    {
        var connectionGeneration = GetCurrentConnectionGeneration();
        var hadRegisteredSession = connectionGeneration != 0
            && (IsConnected || ClientId != 0);
        receiveCancellation?.Cancel();
        CloseTransport(connectionGeneration == 0 ? null : connectionGeneration);
        if (receiveLoopTask is null)
        {
            CleanupIncomingTransfers();
            CancelFinalizations();
            DrainFinalizationsAsync().GetAwaiter().GetResult();
            if (hadRegisteredSession)
            {
                RaiseDisconnectedAsync(connectionGeneration).GetAwaiter().GetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        disposed = true;
        lifecycleLock.Dispose();
        sendLock.Dispose();
        receiveCancellation?.Dispose();
    }

    private async Task ReceiveLoopAsync(
        long connectionGeneration,
        NetworkStream sessionStream,
        CancellationTokenSource sessionCancellation,
        Task startGate)
    {
        var cancellationToken = sessionCancellation.Token;
        try
        {
            await startGate.ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(sessionStream, cancellationToken)
                    .ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                await HandleIncomingFrameAsync(
                        frame,
                        connectionGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);
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
        }
        catch (Exception exception)
        {
            InvokeSubscribersSafely(
                ErrorReceived,
                new ClientErrorEventArgs(exception.Message));
        }
        finally
        {
            var beforeCleanup = BeforeReceiveLoopCleanupAsync;
            if (beforeCleanup is not null)
            {
                await beforeCleanup(connectionGeneration, cancellationToken)
                    .ConfigureAwait(false);
            }

            sessionCancellation.Cancel();
            CleanupIncomingTransfers();
            CancelFinalizations();
            await DrainFinalizationsAsync().ConfigureAwait(false);
            CloseTransport(connectionGeneration);
            await RaiseDisconnectedAsync(connectionGeneration).ConfigureAwait(false);
        }
    }

    private async Task AwaitAndClearReceiveLoopAsync()
    {
        var receiveTask = receiveLoopTask;
        if (receiveTask is null || Task.CurrentId == receiveTask.Id)
        {
            return;
        }

        var cancellation = receiveCancellation;
        Exception? unexpectedFailure = null;
        try
        {
            await IgnoreExpectedShutdownAsync(receiveTask).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            unexpectedFailure = exception;
        }
        finally
        {
            if (ReferenceEquals(receiveLoopTask, receiveTask))
            {
                receiveLoopTask = null;
            }

            if (ReferenceEquals(receiveCancellation, cancellation))
            {
                receiveCancellation = null;
                cancellation?.Dispose();
            }
        }

        if (unexpectedFailure is not null)
        {
            InvokeSubscribersSafely(
                ErrorReceived,
                new ClientErrorEventArgs(unexpectedFailure.Message));
        }
    }

    private async Task HandleIncomingFrameAsync(
        Frame frame,
        long connectionGeneration,
        CancellationToken cancellationToken)
    {
        switch (frame.Command)
        {
            case FrameCommand.ClientList:
                var list = JsonPayload.Deserialize<ClientListPayload>(frame.Payload);
                lock (connectionEventLock)
                {
                    clients = list.Clients.ToArray();
                }
                CleanupTransfersFromDepartedPeers(
                    clients.Select(client => client.Id).ToHashSet());
                InvokeSubscribersSafely(
                    ClientListChanged,
                    new ClientListChangedEventArgs(clients));
                break;
            case FrameCommand.TextMessage:
                string msgId;
                string textContent;
                try
                {
                    var textPayload = JsonPayload.Deserialize<TextMessagePayload>(frame.Payload);
                    msgId = textPayload.MessageId;
                    textContent = textPayload.Text;
                }
                catch
                {
                    msgId = Guid.NewGuid().ToString("N");
                    textContent = Encoding.UTF8.GetString(frame.Payload);
                }
                InvokeSubscribersSafely(
                    MessageReceived,
                    new TextMessageReceivedEventArgs(frame.RouteId, msgId, textContent));
                break;
            case FrameCommand.EditMessage:
                var edit = JsonPayload.Deserialize<EditMessagePayload>(frame.Payload);
                InvokeSubscribersSafely(
                    MessageEdited,
                    new MessageEditedEventArgs(frame.RouteId, edit.MessageId, edit.NewText));
                break;
            case FrameCommand.DeleteMessage:
                var del = JsonPayload.Deserialize<DeleteMessagePayload>(frame.Payload);
                InvokeSubscribersSafely(
                    MessageDeleted,
                    new MessageDeletedEventArgs(frame.RouteId, del.MessageId));
                break;
            case FrameCommand.FileStart:
                await HandleFileStartAsync(
                        frame,
                        connectionGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case FrameCommand.FileChunk:
                await HandleFileChunkAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
            case FrameCommand.FileEnd:
                HandleFileEnd(frame);
                break;
            case FrameCommand.FileAbort:
                HandleFileAbort(frame);
                break;
            case FrameCommand.Error:
                var error = JsonPayload.Deserialize<ErrorPayload>(frame.Payload);
                InvokeSubscribersSafely(
                    ErrorReceived,
                    new ClientErrorEventArgs(error.Message));
                break;
            case FrameCommand.Disconnect:
                receiveCancellation?.Cancel();
                break;
            default:
                InvokeSubscribersSafely(
                    ErrorReceived,
                    new ClientErrorEventArgs("El servidor envió un comando no reconocido."));
                break;
        }
    }

    private async Task HandleFileStartAsync(
        Frame frame,
        long connectionGeneration,
        CancellationToken cancellationToken)
    {
        var start = JsonPayload.Deserialize<FileStartPayload>(frame.Payload);
        if (start.Length < 0)
        {
            throw new InvalidDataException("The incoming file length is invalid.");
        }

        Directory.CreateDirectory(downloadsDirectory);
        var safeName = SanitizeFileName(start.FileName);
        var requestedPath = Path.Combine(downloadsDirectory, safeName);
        var key = (frame.RouteId, start.TransferId);
        if (incomingTransfers.Remove(key, out var previous))
        {
            previous.CancelAndDeleteStaging();
            previous.Dispose();
        }

        var (stagingPath, destination) = CreateStagingFile(start.TransferId);
        var transfer = new IncomingTransfer(
            start.TransferId,
            frame.RouteId,
            safeName,
            requestedPath,
            stagingPath,
            start.Length,
            destination,
            connectionGeneration,
            cancellationToken);
        incomingTransfers[key] = transfer;
        if (File.Exists(requestedPath))
        {
            transfer.ConflictDecision = ResolveFileConflictAsync(
                new FileConflictContext(
                    safeName,
                    requestedPath,
                    frame.RouteId,
                    start.TransferId),
                transfer.CancellationToken);
        }

        await EmitFileProgressAsync(
                connectionGeneration,
                start.TransferId,
                frame.RouteId,
                safeName,
                start.Length == 0 ? 100 : 0,
                false)
            .ConfigureAwait(false);
    }

    private async Task HandleFileChunkAsync(Frame frame, CancellationToken cancellationToken)
    {
        var (transferId, chunk) = FileChunkPayload.Parse(frame.Payload);
        var key = (frame.RouteId, transferId);
        if (!incomingTransfers.TryGetValue(key, out var transfer))
        {
            throw new InvalidDataException("A file chunk arrived before its start frame.");
        }

        if (transfer.BytesWritten + chunk.Length > transfer.ExpectedLength)
        {
            incomingTransfers.Remove(key);
            transfer.CancelAndDeleteStaging();
            transfer.Dispose();
            throw new InvalidDataException("The incoming file exceeded its declared length.");
        }

        await transfer.Stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        transfer.BytesWritten += chunk.Length;
        var percentage = CalculatePercentage(transfer.BytesWritten, transfer.ExpectedLength);
        if (percentage != transfer.LastReportedPercentage || transfer.BytesWritten == transfer.ExpectedLength)
        {
            transfer.LastReportedPercentage = percentage;
            await EmitFileProgressAsync(
                    transfer.ConnectionGeneration,
                    transfer.TransferId,
                    transfer.SenderId,
                    transfer.FileName,
                    percentage,
                    false)
                .ConfigureAwait(false);
        }
    }

    private void HandleFileEnd(Frame frame)
    {
        var end = JsonPayload.Deserialize<FileEndPayload>(frame.Payload);
        var key = (frame.RouteId, end.TransferId);
        if (!incomingTransfers.Remove(key, out var transfer))
        {
            throw new InvalidDataException("A file end arrived before its start frame.");
        }

        StartFinalization(key, transfer);
    }

    private void HandleFileAbort(Frame frame)
    {
        var abort = JsonPayload.Deserialize<FileAbortPayload>(frame.Payload);
        var key = (frame.RouteId, abort.TransferId);
        if (incomingTransfers.Remove(key, out var incoming))
        {
            incoming.CancelAndDeleteStaging();
            incoming.Dispose();
            return;
        }

        lock (finalizationsLock)
        {
            if (finalizations.TryGetValue(key, out var finalization))
            {
                finalization.Transfer.Cancel();
            }
        }
    }

    private async Task SendFrameAsync(
        Frame frame,
        CancellationToken cancellationToken,
        long? expectedConnectionGeneration = null)
    {
        NetworkStream activeStream;
        lock (connectionEventLock)
        {
            if (expectedConnectionGeneration is long expected
                && (expected != activeConnectionGeneration
                    || expected == disconnectedConnectionGeneration))
            {
                throw new InvalidOperationException("The connection session is no longer active.");
            }

            activeStream = stream
                ?? throw new InvalidOperationException("The client is not connected.");
        }

        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(activeStream, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task<bool> TrySendFileAbortAsync(
        byte targetId,
        Guid transferId,
        long connectionGeneration)
    {
        if (!IsConnectionGenerationActive(connectionGeneration))
        {
            return false;
        }

        var overrideSend = TrySendFileAbortAsyncOverride;
        if (overrideSend is not null)
        {
            return await overrideSend(transferId, CancellationToken.None)
                .ConfigureAwait(false);
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await SendFrameAsync(
                    new Frame(
                        FrameCommand.FileAbort,
                        targetId,
                        JsonPayload.Serialize(new FileAbortPayload(transferId))),
                    timeout.Token,
                    connectionGeneration)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or IOException
                or SocketException
                or ObjectDisposedException
                or InvalidOperationException)
        {
            return false;
        }
    }

    private long GetConnectedGeneration()
    {
        lock (connectionEventLock)
        {
            if (!IsConnected
                || stream is null
                || activeConnectionGeneration == 0
                || activeConnectionGeneration == disconnectedConnectionGeneration)
            {
                throw new InvalidOperationException("The client is not connected.");
            }

            return activeConnectionGeneration;
        }
    }

    private long GetCurrentConnectionGeneration()
    {
        lock (connectionEventLock)
        {
            return activeConnectionGeneration;
        }
    }

    private bool IsConnectionGenerationActive(long connectionGeneration)
    {
        lock (connectionEventLock)
        {
            return connectionGeneration != 0
                && connectionGeneration == activeConnectionGeneration
                && connectionGeneration != disconnectedConnectionGeneration;
        }
    }

    private void AbortConnectionGeneration(long connectionGeneration)
    {
        CancellationTokenSource? cancellation;
        lock (connectionEventLock)
        {
            if (connectionGeneration == 0
                || connectionGeneration != activeConnectionGeneration
                || connectionGeneration == disconnectedConnectionGeneration)
            {
                return;
            }

            cancellation = receiveCancellation;
        }

        cancellation?.Cancel();
        CloseTransport(connectionGeneration);
    }

    private void CloseTransport(long? expectedConnectionGeneration = null)
    {
        TcpClient? client;
        lock (connectionEventLock)
        {
            if (expectedConnectionGeneration is long expected
                && expected != activeConnectionGeneration)
            {
                return;
            }

            IsConnected = false;
            ClientId = 0;
            stream = null;
            client = tcpClient;
            tcpClient = null;
        }

        client?.Close();
    }

    private Task RaiseDisconnectedAsync(long connectionGeneration)
    {
        return EnqueueConnectionEvent(
            connectionGeneration,
            isDisconnect: true,
            () => InvokeSubscribersSafely(Disconnected, EventArgs.Empty));
    }

    private void InvokeSubscribersSafely<TEventArgs>(
        EventHandler<TEventArgs>? subscribers,
        TEventArgs eventArgs)
        where TEventArgs : EventArgs
    {
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, eventArgs);
            }
            catch (Exception)
            {
            }
        }
    }

    private void InvokeSubscribersSafely(EventHandler? subscribers, EventArgs eventArgs)
    {
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, eventArgs);
            }
            catch (Exception)
            {
            }
        }
    }

    private void CleanupIncomingTransfers()
    {
        foreach (var transfer in incomingTransfers.Values)
        {
            transfer.CancelAndDeleteStaging();
            transfer.Dispose();
        }

        incomingTransfers.Clear();
    }

    private void CleanupTransfersFromDepartedPeers(HashSet<byte> activeClientIds)
    {
        var departedTransfers = incomingTransfers.Keys
            .Where(key => !activeClientIds.Contains(key.SenderId))
            .ToArray();
        foreach (var key in departedTransfers)
        {
            if (incomingTransfers.Remove(key, out var transfer))
            {
                transfer.CancelAndDeleteStaging();
                transfer.Dispose();
            }
        }

        lock (finalizationsLock)
        {
            foreach (var (key, finalization) in finalizations)
            {
                if (!activeClientIds.Contains(key.SenderId))
                {
                    finalization.Transfer.Cancel();
                }
            }
        }
    }

    private void CancelFinalizations()
    {
        lock (finalizationsLock)
        {
            foreach (var finalization in finalizations.Values)
            {
                finalization.Transfer.Cancel();
            }
        }
    }

    private async Task DrainFinalizationsAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (finalizationsLock)
            {
                tasks = finalizations.Values
                    .Select(finalization => finalization.Task)
                    .ToArray();
            }

            if (tasks.Length == 0)
            {
                return;
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private Task EmitFileProgressAsync(
        long connectionGeneration,
        Guid transferId,
        byte peerId,
        string fileName,
        int percentage,
        bool isOutgoing)
    {
        return EnqueueConnectionEvent(
            connectionGeneration,
            isDisconnect: false,
            () => InvokeSubscribersSafely(
                FileProgressChanged,
                new FileProgressEventArgs(
                    transferId,
                    peerId,
                    fileName,
                    percentage,
                    isOutgoing)));
    }

    private Task EmitFileReceivedAsync(
        long connectionGeneration,
        FileReceivedEventArgs eventArgs)
    {
        return EnqueueConnectionEvent(
            connectionGeneration,
            isDisconnect: false,
            () => InvokeSubscribersSafely(FileReceived, eventArgs));
    }

    private Task EnqueueConnectionEvent(
        long connectionGeneration,
        bool isDisconnect,
        Action callback)
    {
        ConnectionEventWorkItem? workItem = null;
        var shouldDrain = false;
        lock (connectionEventLock)
        {
            if (connectionGeneration == 0
                || connectionGeneration != activeConnectionGeneration
                || connectionGeneration == disconnectedConnectionGeneration)
            {
                return Task.CompletedTask;
            }

            if (isDisconnect)
            {
                disconnectedConnectionGeneration = connectionGeneration;
            }

            workItem = new ConnectionEventWorkItem(callback);
            connectionEvents.Enqueue(workItem);
            if (!dispatchingConnectionEvents)
            {
                dispatchingConnectionEvents = true;
                shouldDrain = true;
            }
        }

        if (shouldDrain)
        {
            DrainConnectionEvents();
        }

        return workItem.Completion.Task;
    }

    private void DrainConnectionEvents()
    {
        while (true)
        {
            ConnectionEventWorkItem workItem;
            lock (connectionEventLock)
            {
                if (connectionEvents.Count == 0)
                {
                    dispatchingConnectionEvents = false;
                    return;
                }

                workItem = connectionEvents.Dequeue();
            }

            try
            {
                workItem.Callback();
                workItem.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                workItem.Completion.TrySetException(exception);
            }
        }
    }

    private (string Path, FileStream Stream) CreateStagingFile(Guid transferId)
    {
        for (var attempt = 0; ; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt}";
            var path = Path.Combine(
                downloadsDirectory,
                $".chat-transfer-{transferId:N}{suffix}.tmp");
            try
            {
                return (path, OpenDestinationFile(path, FileMode.CreateNew));
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
    }

    private Task<FileConflictDecision> ResolveFileConflictAsync(
        FileConflictContext conflict,
        CancellationToken cancellationToken)
    {
        var resolver = fileConflictResolver;
        if (resolver is null)
        {
            return ObserveConflictDecision(
                Task.FromException<FileConflictDecision>(
                    new IOException(
                        $"The destination file '{conflict.FileName}' already exists and no conflict resolver was provided.")));
        }

        try
        {
            return ObserveConflictDecision(
                resolver.ResolveAsync(conflict, cancellationToken).AsTask());
        }
        catch (Exception exception)
        {
            return ObserveConflictDecision(
                Task.FromException<FileConflictDecision>(exception));
        }
    }

    private static Task<FileConflictDecision> ObserveConflictDecision(
        Task<FileConflictDecision> decision)
    {
        _ = decision.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return decision;
    }

    private void StartFinalization(
        (byte SenderId, Guid TransferId) key,
        IncomingTransfer transfer)
    {
        var operation = new FinalizationOperation(transfer);
        lock (finalizationsLock)
        {
            finalizations[key] = operation;
            operation.Task = FinalizeIncomingTransferAsync(key, operation);
        }
    }

    private async Task FinalizeIncomingTransferAsync(
        (byte SenderId, Guid TransferId) key,
        FinalizationOperation operation)
    {
        await Task.Yield();
        var transfer = operation.Transfer;
        try
        {
            if (transfer.BytesWritten != transfer.ExpectedLength)
            {
                throw new InvalidDataException("The incoming file was incomplete.");
            }

            await transfer.Stream.FlushAsync(transfer.CancellationToken).ConfigureAwait(false);
            await transfer.Stream.DisposeAsync().ConfigureAwait(false);
            transfer.MarkStreamClosed();
            var beforeCommit = BeforeIncomingFileCommitAsync;
            if (beforeCommit is not null)
            {
                await beforeCommit(transfer.TransferId, transfer.CancellationToken)
                    .ConfigureAwait(false);
            }

            var (fileName, filePath) = await CommitStagingFileAsync(transfer)
                .ConfigureAwait(false);
            transfer.CancellationToken.ThrowIfCancellationRequested();
            await EmitFileProgressAsync(
                    transfer.ConnectionGeneration,
                    transfer.TransferId,
                    transfer.SenderId,
                    fileName,
                    100,
                    false)
                .ConfigureAwait(false);
            await EmitFileReceivedAsync(
                    transfer.ConnectionGeneration,
                    new FileReceivedEventArgs(
                        transfer.TransferId,
                        transfer.SenderId,
                        fileName,
                        filePath))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            transfer.CancellationToken.IsCancellationRequested
            && exception is OperationCanceledException
                or ObjectDisposedException
                or IOException)
        {
        }
        catch (Exception exception)
        {
            InvokeSubscribersSafely(
                ErrorReceived,
                new ClientErrorEventArgs(exception.Message));
        }
        finally
        {
            transfer.CancelAndDeleteStaging();
            lock (finalizationsLock)
            {
                if (finalizations.TryGetValue(key, out var current)
                    && ReferenceEquals(current, operation))
                {
                    finalizations.Remove(key);
                }
            }

            transfer.Dispose();
        }
    }

    private async Task<(string FileName, string FilePath)> CommitStagingFileAsync(
        IncomingTransfer transfer)
    {
        transfer.CancellationToken.ThrowIfCancellationRequested();
        var decisionTask = transfer.ConflictDecision;
        if (decisionTask is null)
        {
            try
            {
                File.Move(transfer.StagingPath, transfer.RequestedPath);
                return (transfer.FileName, transfer.RequestedPath);
            }
            catch (IOException) when (File.Exists(transfer.RequestedPath))
            {
                decisionTask = ResolveFileConflictAsync(
                    new FileConflictContext(
                        transfer.FileName,
                        transfer.RequestedPath,
                        transfer.SenderId,
                        transfer.TransferId),
                    transfer.CancellationToken);
            }
        }

        var decision = await decisionTask.ConfigureAwait(false);
        transfer.CancellationToken.ThrowIfCancellationRequested();
        if (decision == FileConflictDecision.Replace)
        {
            File.Move(transfer.StagingPath, transfer.RequestedPath, true);
            return (transfer.FileName, transfer.RequestedPath);
        }

        return MoveStagingToFirstAvailableName(transfer);
    }

    private (string FileName, string FilePath) MoveStagingToFirstAvailableName(
        IncomingTransfer transfer)
    {
        var stem = Path.GetFileNameWithoutExtension(transfer.FileName);
        var extension = Path.GetExtension(transfer.FileName);
        for (var suffix = 1; ; suffix++)
        {
            transfer.CancellationToken.ThrowIfCancellationRequested();
            var candidateName = $"{stem} ({suffix}){extension}";
            var candidatePath = Path.Combine(downloadsDirectory, candidateName);
            try
            {
                File.Move(transfer.StagingPath, candidatePath);
                return (candidateName, candidatePath);
            }
            catch (IOException) when (
                File.Exists(candidatePath)
                && File.Exists(transfer.StagingPath))
            {
            }
        }
    }

    private static FileStream OpenDestinationFile(string path, FileMode mode)
    {
        return new FileStream(
            path,
            mode,
            FileAccess.Write,
            FileShare.None,
            FileChunkPayload.MaximumChunkLength,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return "archivo";
        }

        var name = Path.GetFileName(fileName);
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(
            character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        sanitized = sanitized.TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "archivo";
        }

        var normalizedStem = Path.GetFileNameWithoutExtension(sanitized)
            .TrimEnd(' ', '.');
        return WindowsDeviceNames.Contains(normalizedStem)
            ? $"_{sanitized}"
            : sanitized;
    }

    private static int CalculatePercentage(long transferred, long total)
    {
        return total == 0 ? 100 : (int)Math.Clamp(transferred * 100L / total, 0, 100);
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

    private sealed class ConnectionEventWorkItem(Action callback)
    {
        public Action Callback { get; } = callback;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class IncomingTransfer(
        Guid transferId,
        byte senderId,
        string fileName,
        string requestedPath,
        string stagingPath,
        long expectedLength,
        FileStream stream,
        long connectionGeneration,
        CancellationToken lifecycleCancellation) : IDisposable
    {
        private readonly CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(lifecycleCancellation);
        private bool streamClosed;

        public Guid TransferId { get; } = transferId;

        public byte SenderId { get; } = senderId;

        public string FileName { get; } = fileName;

        public string RequestedPath { get; } = requestedPath;

        public string StagingPath { get; } = stagingPath;

        public long ExpectedLength { get; } = expectedLength;

        public FileStream Stream { get; } = stream;

        public long ConnectionGeneration { get; } = connectionGeneration;

        public long BytesWritten { get; set; }

        public int LastReportedPercentage { get; set; } = -1;

        public CancellationToken CancellationToken => cancellation.Token;

        public Task<FileConflictDecision>? ConflictDecision { get; set; }

        public void Cancel()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void MarkStreamClosed()
        {
            streamClosed = true;
        }

        public void CancelAndDeleteStaging()
        {
            Cancel();
            try
            {
                if (!streamClosed)
                {
                    Stream.Dispose();
                    streamClosed = true;
                }
            }
            finally
            {
                try
                {
                    File.Delete(StagingPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        public void Dispose()
        {
            cancellation.Dispose();
        }
    }

    private sealed class FinalizationOperation(IncomingTransfer transfer)
    {
        public IncomingTransfer Transfer { get; } = transfer;

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
