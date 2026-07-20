using System.Net.Sockets;
using Chat.Protocol;
using ChatCliente.Network;

using ChatCliente.Persistence;

namespace ChatCliente.Application;

public sealed class ClientCoordinator : IAsyncDisposable
{
    private readonly LoginForm loginForm;
    private readonly Func<string, IChatClient> clientFactory;
    private readonly LocalHistoryService historyService = new();
    private readonly Dictionary<string, List<ChatMessageView>> conversationHistories = new(StringComparer.OrdinalIgnoreCase);
    private string? currentUsername;
    private string currentHost = "127.0.0.1";
    private int currentPort = 55000;
    private bool disposed;

    public ClientCoordinator(
        LoginForm loginForm,
        Func<string, IChatClient>? clientFactory = null)
    {
        this.loginForm = loginForm ?? throw new ArgumentNullException(nameof(loginForm));
        this.clientFactory = clientFactory
            ?? new Func<string, IChatClient>(downloadsDirectory => new ChatClient(
                downloadsDirectory,
                new UiFileConflictResolver(
                    () => (Form?)ChatForm ?? this.loginForm)));
        loginForm.ConnectRequested += HandleConnectRequested;
        IsWired = true;
    }

    public event EventHandler<ChatForm>? ChatOpened;

    public bool IsWired { get; }

    public IChatClient? Client { get; private set; }

    public ChatForm? ChatForm { get; private set; }

    public async Task<bool> ConnectAsync(
        string username,
        string address,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        loginForm.ClearInlineError();
        if (!TryParseEndpoint(address, out var host, out var port))
        {
            loginForm.ShowInlineError(
                "Usa una dirección y puerto, por ejemplo 127.0.0.1:55000.");
            return false;
        }

        loginForm.SetBusy(true);
        IChatClient? client = null;
        try
        {
            var downloadsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "Chat de Redes");
            client = clientFactory(downloadsDirectory);
            Client = client;
            WireClient(client);
            var result = await client
                .ConnectAndRegisterAsync(host, port, username, cancellationToken)
                .ConfigureAwait(true);
            if (!result.Accepted)
            {
                if (string.Equals(
                        result.Error,
                        ProtocolMessages.DuplicateUsername,
                        StringComparison.Ordinal))
                {
                    loginForm.ShowUsernameAlreadyInUse();
                }
                else
                {
                    loginForm.ShowInlineError(result.Error ?? "El servidor rechazó la conexión.");
                }

                await ReleaseClientAsync(client).ConfigureAwait(true);
                return false;
            }

            currentUsername = username;
            currentHost = host;
            currentPort = port;
            conversationHistories.Clear();
            var savedHistory = historyService.LoadHistory(username);
            foreach (var conv in savedHistory)
            {
                var list = conv.Messages.Select(m => new ChatMessageView(
                    m.Id, m.Sender, m.Text, m.Timestamp, m.IsOwn, m.IsEdited, m.IsDeleted)).ToList();
                conversationHistories[conv.PeerUsername] = list;
            }

            var chatForm = new ChatForm(username);
            ChatForm = chatForm;
            WireChatForm(chatForm);
            chatForm.SetCurrentUser(username);
            chatForm.SetConnectionState(true, "Conectado");

            if (client.Clients.Count > 0)
            {
                var users = client.Clients
                    .Where(user => user.Id != client.ClientId)
                    .Select(user => new ChatUserView(user.Id, user.Username.Trim(), true))
                    .DistinctBy(user => user.Id)
                    .DistinctBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                chatForm.SetUsers(users);
                RestoreHistoryForUsers(client.Clients, chatForm);
            }

            loginForm.Hide();
            chatForm.Show();
            ChatOpened?.Invoke(this, chatForm);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (client is not null)
            {
                await ReleaseClientAsync(client).ConfigureAwait(true);
            }

            throw;
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or TimeoutException)
        {
            loginForm.ShowInlineError(
                "No se pudo conectar al servidor. Verifica la dirección y el puerto.");
            if (client is not null)
            {
                await ReleaseClientAsync(client).ConfigureAwait(true);
            }

            return false;
        }
        catch (Exception exception)
        {
            loginForm.ShowInlineError($"No se pudo conectar: {exception.Message}");
            if (client is not null)
            {
                await ReleaseClientAsync(client).ConfigureAwait(true);
            }

            return false;
        }
        finally
        {
            if (!loginForm.IsDisposed)
            {
                loginForm.SetBusy(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        loginForm.ConnectRequested -= HandleConnectRequested;
        var chatForm = ChatForm;
        if (chatForm is not null)
        {
            UnwireChatForm(chatForm);
            if (!chatForm.IsDisposed)
            {
                chatForm.Close();
            }

            ChatForm = null;
        }

        var client = Client;
        if (client is not null)
        {
            await ReleaseClientAsync(client).ConfigureAwait(false);
        }
    }

    public static bool TryParseEndpoint(string address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var separator = address.LastIndexOf(':');
        if (separator <= 0
            || separator == address.Length - 1
            || !int.TryParse(address[(separator + 1)..], out port)
            || port is <= 0 or > ushort.MaxValue)
        {
            return false;
        }

        host = address[..separator].Trim();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return host.Length > 0;
    }

    public async Task<IReadOnlyList<FileSendFailure>> SendFilesAsync(
        byte targetId,
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(filePaths);
        var client = Client ?? throw new InvalidOperationException("The client is not connected.");
        var sends = filePaths
            .Select(path => SendFileWithFailureAsync(client, targetId, path, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(sends).ConfigureAwait(false);
        return results
            .Where(failure => failure is not null)
            .Cast<FileSendFailure>()
            .ToArray();
    }

    private async void HandleConnectRequested(object? sender, LoginRequestedEventArgs args)
    {
        try
        {
            await ConnectAsync(args.UserName, args.ServerAddress);
        }
        catch (OperationCanceledException) when (disposed)
        {
        }
        catch (Exception exception)
        {
            if (!loginForm.IsDisposed)
            {
                loginForm.ShowInlineError($"No se pudo conectar: {exception.Message}");
                loginForm.SetBusy(false);
            }
        }
    }

    private async void HandleSendMessageRequested(
        object? sender,
        MessageRequestedEventArgs args)
    {
        var client = Client;
        if (client is null)
        {
            return;
        }

        var targetId = args.TargetId;
        var text = args.Message;
        try
        {
            var messageId = Guid.NewGuid().ToString("N");
            await client.SendMessageAsync(targetId, messageId, text);
            var message = new ChatMessageView(
                messageId,
                "Tú",
                text,
                DateTimeOffset.Now,
                true);
            UpdateChatForm(
                form =>
                {
                    form.AppendMessage(targetId, message);
                    if (form.SelectedRecipientId == targetId)
                    {
                        form.ClearMessageDraft();
                    }
                });
            RecordMessageInMemoryAndDisk(targetId, message);
        }
        catch (OperationCanceledException) when (disposed || !client.IsConnected)
        {
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private async void HandleAttachmentRequested(
        object? sender,
        AttachmentRequestedEventArgs args)
    {
        var client = Client;
        if (client is null)
        {
            return;
        }

        var targetId = args.TargetId;
        try
        {
            var failures = await SendFilesAsync(targetId, args.FilePaths);
            if (failures.Count > 0)
            {
                var message = string.Join(
                    Environment.NewLine,
                    failures.Select(failure =>
                        $"{Path.GetFileName(failure.FilePath)}: {failure.Error}"));
                UpdateChatForm(form => form.ShowComposerError(message));
            }
        }
        catch (OperationCanceledException) when (disposed || !client.IsConnected)
        {
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleClientListChanged(object? sender, ClientListChangedEventArgs args)
    {
        try
        {
            var client = Client;
            var users = args.Clients
                .Where(user => client is null || user.Id != client.ClientId)
                .Select(user => new ChatUserView(user.Id, user.Username.Trim(), true))
                .DistinctBy(user => user.Id)
                .DistinctBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            UpdateChatForm(form =>
            {
                form.SetUsers(users);
                RestoreHistoryForUsers(args.Clients, form);
            });
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleMessageReceived(object? sender, TextMessageReceivedEventArgs args)
    {
        try
        {
            var senderName = Client?.Clients
                .FirstOrDefault(client => client.Id == args.SenderId)?.Username
                ?? $"Usuario {args.SenderId}";
            var message = new ChatMessageView(
                args.MessageId,
                senderName,
                args.Text,
                DateTimeOffset.Now,
                false);
            UpdateChatForm(form => form.AppendMessage(args.SenderId, message));
            RecordMessageInMemoryAndDisk(args.SenderId, message);
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleFileProgressChanged(object? sender, FileProgressEventArgs args)
    {
        try
        {
            var status = args.IsOutgoing
                ? (args.Percentage >= 100 ? $"Enviado: {args.FileName}" : $"Enviando {args.FileName}... {args.Percentage}%")
                : (args.Percentage >= 100 ? $"Recibido: {args.FileName}" : $"Recibiendo {args.FileName}... {args.Percentage}%");
            var transfer = new FileTransferView(
                args.TransferId.ToString("N"),
                args.FileName,
                args.Percentage,
                status,
                args.IsOutgoing);
            UpdateChatForm(form => form.AddOrUpdateFileTransfer(args.PeerId, transfer));
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleFileReceived(object? sender, FileReceivedEventArgs args)
    {
        try
        {
            var transfer = new FileTransferView(
                args.TransferId.ToString("N"),
                args.FileName,
                100,
                $"Recibido: {args.FileName}",
                false);
            UpdateChatForm(form => form.AddOrUpdateFileTransfer(args.SenderId, transfer));
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleErrorReceived(object? sender, ClientErrorEventArgs args)
    {
        UpdateChatForm(form => form.ShowComposerError(args.Message));
    }

    private void HandleDisconnected(object? sender, EventArgs args)
    {
        UpdateChatForm(
            form =>
            {
                form.SetConnectionState(false, "Sin conexión");
                form.ShowComposerError("Se perdió la conexión con el servidor.");
            });
    }

    private async void HandleChatFormClosed(object? sender, FormClosedEventArgs args)
    {
        var client = Client;
        if (client is null)
        {
            return;
        }

        try
        {
            await ReleaseClientAsync(client);
        }
        catch (Exception exception)
        {
            if (!loginForm.IsDisposed)
            {
                loginForm.ShowInlineError($"No se pudo cerrar la conexión: {exception.Message}");
            }
        }
    }

    private void WireClient(IChatClient client)
    {
        client.ClientListChanged += HandleClientListChanged;
        client.MessageReceived += HandleMessageReceived;
        client.MessageEdited += HandleMessageEdited;
        client.MessageDeleted += HandleMessageDeleted;
        client.GroupCreated += HandleGroupCreated;
        client.GroupMessageReceived += HandleGroupMessageReceived;
        client.VoiceNoteReceived += HandleVoiceNoteReceived;
        client.CallOffered += HandleCallOffered;
        client.CallAnswered += HandleCallAnswered;
        client.CallEnded += HandleCallEnded;
        client.FileProgressChanged += HandleFileProgressChanged;
        client.FileReceived += HandleFileReceived;
        client.ErrorReceived += HandleErrorReceived;
        client.Disconnected += HandleDisconnected;
    }

    private void UnwireClient(IChatClient client)
    {
        client.ClientListChanged -= HandleClientListChanged;
        client.MessageReceived -= HandleMessageReceived;
        client.MessageEdited -= HandleMessageEdited;
        client.MessageDeleted -= HandleMessageDeleted;
        client.GroupCreated -= HandleGroupCreated;
        client.GroupMessageReceived -= HandleGroupMessageReceived;
        client.VoiceNoteReceived -= HandleVoiceNoteReceived;
        client.CallOffered -= HandleCallOffered;
        client.CallAnswered -= HandleCallAnswered;
        client.CallEnded -= HandleCallEnded;
        client.FileProgressChanged -= HandleFileProgressChanged;
        client.FileReceived -= HandleFileReceived;
        client.ErrorReceived -= HandleErrorReceived;
        client.Disconnected -= HandleDisconnected;
    }

    private void WireChatForm(ChatForm chatForm)
    {
        chatForm.SendMessageRequested += HandleSendMessageRequested;
        chatForm.EditMessageRequested += HandleEditMessageRequested;
        chatForm.DeleteMessageRequested += HandleDeleteMessageRequested;
        chatForm.CreateGroupRequested += HandleCreateGroupRequested;
        chatForm.SendGroupMessageRequested += HandleSendGroupMessageRequested;
        chatForm.SendVoiceNoteRequested += HandleSendVoiceNoteRequested;
        chatForm.StartCallRequested += HandleStartCallRequested;
        chatForm.AnswerCallRequested += HandleAnswerCallRequested;
        chatForm.EndCallRequested += HandleEndCallRequested;
        chatForm.AttachmentRequested += HandleAttachmentRequested;
        chatForm.FormClosed += HandleChatFormClosed;
    }

    private void UnwireChatForm(ChatForm chatForm)
    {
        chatForm.SendMessageRequested -= HandleSendMessageRequested;
        chatForm.EditMessageRequested -= HandleEditMessageRequested;
        chatForm.DeleteMessageRequested -= HandleDeleteMessageRequested;
        chatForm.CreateGroupRequested -= HandleCreateGroupRequested;
        chatForm.SendGroupMessageRequested -= HandleSendGroupMessageRequested;
        chatForm.SendVoiceNoteRequested -= HandleSendVoiceNoteRequested;
        chatForm.StartCallRequested -= HandleStartCallRequested;
        chatForm.AnswerCallRequested -= HandleAnswerCallRequested;
        chatForm.EndCallRequested -= HandleEndCallRequested;
        chatForm.AttachmentRequested -= HandleAttachmentRequested;
        chatForm.FormClosed -= HandleChatFormClosed;
    }

    private async Task ReleaseClientAsync(IChatClient client)
    {
        UnwireClient(client);
        if (ReferenceEquals(Client, client))
        {
            Client = null;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!loginForm.IsDisposed)
            {
                loginForm.ShowInlineError($"No se pudo cerrar la conexión: {exception.Message}");
            }
        }
    }

    private static async Task<FileSendFailure?> SendFileWithFailureAsync(
        IChatClient client,
        byte targetId,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.SendFileAsync(targetId, filePath, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new FileSendFailure(filePath, exception.Message);
        }
    }

    private void UpdateChatForm(Action<ChatForm> update)
    {
        var chatForm = ChatForm;
        if (chatForm is null || chatForm.IsDisposed)
        {
            return;
        }

        if (chatForm.InvokeRequired)
        {
            try
            {
                chatForm.BeginInvoke(() => ExecuteChatUpdate(chatForm, update));
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        ExecuteChatUpdate(chatForm, update);
    }

    private static void ExecuteChatUpdate(ChatForm chatForm, Action<ChatForm> update)
    {
        if (chatForm.IsDisposed)
        {
            return;
        }

        try
        {
            update(chatForm);
        }
        catch (Exception exception)
        {
            chatForm.ShowComposerError($"No se pudo actualizar el chat: {exception.Message}");
        }
    }

    private async void HandleEditMessageRequested(object? sender, EditMessageRequestedEventArgs args)
    {
        var client = Client;
        if (client is null) return;
        try
        {
            await client.SendEditMessageAsync(args.TargetId, args.MessageId, args.NewText);
            UpdateChatForm(form => form.UpdateEditedMessage(args.TargetId, args.MessageId, args.NewText));
            UpdateLocalHistoryEdited(args.TargetId, args.MessageId, args.NewText);
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private async void HandleDeleteMessageRequested(object? sender, DeleteMessageRequestedEventArgs args)
    {
        var client = Client;
        if (client is null) return;
        try
        {
            await client.SendDeleteMessageAsync(args.TargetId, args.MessageId);
            UpdateChatForm(form => form.MarkMessageDeleted(args.TargetId, args.MessageId));
            UpdateLocalHistoryDeleted(args.TargetId, args.MessageId);
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleMessageEdited(object? sender, MessageEditedEventArgs args)
    {
        UpdateChatForm(form => form.UpdateEditedMessage(args.SenderId, args.MessageId, args.NewText));
        UpdateLocalHistoryEdited(args.SenderId, args.MessageId, args.NewText);
    }

    private void HandleMessageDeleted(object? sender, MessageDeletedEventArgs args)
    {
        UpdateChatForm(form => form.MarkMessageDeleted(args.SenderId, args.MessageId));
        UpdateLocalHistoryDeleted(args.SenderId, args.MessageId);
    }

    private void HandleCreateGroupRequested(object? sender, EventArgs args)
    {
        var client = Client;
        if (client is null || !client.IsConnected) return;

        var availableClients = client.Clients.Where(c => c.Id != client.ClientId).ToList();
        using var dialog = new CreateGroupDialog(availableClients);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                _ = client.CreateGroupAsync(dialog.GroupName, dialog.SelectedMemberIds);
            }
            catch (Exception exception)
            {
                UpdateChatForm(form => form.ShowComposerError($"No se pudo crear el grupo: {exception.Message}"));
            }
        }
    }

    private void HandleGroupCreated(object? sender, GroupCreatedEventArgs args)
    {
        var memberNames = args.Members.Select(m => m.Username).ToList();
        var groupView = new ChatGroupView(args.GroupId, args.GroupName, memberNames);
        UpdateChatForm(form => form.AddGroup(groupView));
    }

    private async void HandleSendGroupMessageRequested(object? sender, SendGroupMessageRequestedEventArgs args)
    {
        var client = Client;
        if (client is null || !client.IsConnected) return;

        try
        {
            var messageId = Guid.NewGuid().ToString("N");
            await client.SendGroupMessageAsync(args.GroupId, messageId, args.Message);
            var message = new ChatMessageView(
                messageId,
                "Tú",
                args.Message,
                DateTimeOffset.Now,
                true);
            UpdateChatForm(form => form.AppendGroupMessage(args.GroupId, message));
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleGroupMessageReceived(object? sender, GroupMessageReceivedEventArgs args)
    {
        try
        {
            var senderName = Client?.Clients
                .FirstOrDefault(client => client.Id == args.SenderId)?.Username
                ?? $"Usuario {args.SenderId}";
            var message = new ChatMessageView(
                args.MessageId,
                senderName,
                args.Text,
                DateTimeOffset.Now,
                false);
            UpdateChatForm(form => form.AppendGroupMessage(args.GroupId, message));
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private Media.UdpAudioStreamer? activeUdpStreamer;

    private async void HandleSendVoiceNoteRequested(object? sender, SendVoiceNoteRequestedEventArgs args)
    {
        var client = Client;
        if (client is null || !client.IsConnected) return;

        try
        {
            var vnId = Guid.NewGuid().ToString("N");
            var seconds = Math.Max(1, args.DurationMs / 1000);
            await client.SendVoiceNoteAsync(args.TargetId, vnId, args.DurationMs, $"voicenote_{vnId}.wav", args.AudioData);
            var noteView = new VoiceNoteView(vnId, "Tú", $"{seconds}s", args.AudioData, true);
            UpdateChatForm(form => form.AppendVoiceNote(args.TargetId, noteView));
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleVoiceNoteReceived(object? sender, VoiceNoteReceivedEventArgs args)
    {
        try
        {
            var senderName = Client?.Clients.FirstOrDefault(c => c.Id == args.SenderId)?.Username ?? $"Usuario {args.SenderId}";
            var seconds = Math.Max(1, args.DurationMs / 1000);
            var noteView = new VoiceNoteView(args.VoiceNoteId, senderName, $"{seconds}s", args.AudioData, false);
            UpdateChatForm(form => form.AppendVoiceNote(args.SenderId, noteView));
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private async void HandleStartCallRequested(object? sender, CallRequestedEventArgs args)
    {
        var client = Client;
        if (client is null || !client.IsConnected) return;

        try
        {
            var callId = Guid.NewGuid();
            if (activeUdpStreamer is not null)
            {
                await activeUdpStreamer.DisposeAsync();
            }
            activeUdpStreamer = new Media.UdpAudioStreamer();
            await client.SendCallOfferAsync(args.TargetId, callId, currentUsername ?? "Usuario", activeUdpStreamer.LocalPort);
            UpdateChatForm(form => form.ShowComposerError("Llamando... Espere respuesta."));
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleCallOffered(object? sender, CallOfferEventArgs args)
    {
        UpdateChatForm(form => form.ShowIncomingCallDialog(args.CallerId, args.CallId, args.CallerName));
    }

    private async void HandleAnswerCallRequested(object? sender, CallAnsweredRequestedEventArgs args)
    {
        var client = Client;
        if (client is null || !client.IsConnected) return;

        try
        {
            if (activeUdpStreamer is not null)
            {
                await activeUdpStreamer.DisposeAsync();
            }
            activeUdpStreamer = new Media.UdpAudioStreamer();
            await client.SendCallAnswerAsync(args.TargetId, args.CallId, args.Accepted, null, activeUdpStreamer.LocalPort);
            if (args.Accepted)
            {
                var peerName = client.Clients.FirstOrDefault(c => c.Id == args.TargetId)?.Username ?? $"Usuario {args.TargetId}";
                activeUdpStreamer.StartStreaming(currentHost, currentPort + 1, client.ClientId, args.TargetId);
                UpdateChatForm(form => form.ShowActiveCallBanner(args.CallId, peerName));
            }
        }
        catch (Exception exception)
        {
            UpdateChatForm(form => form.ShowComposerError(exception.Message));
        }
    }

    private void HandleCallAnswered(object? sender, CallAnswerEventArgs args)
    {
        if (args.Accepted && Client is not null && activeUdpStreamer is not null)
        {
            var peerName = Client.Clients.FirstOrDefault(c => c.Id == args.ResponderId)?.Username ?? $"Usuario {args.ResponderId}";
            activeUdpStreamer.StartStreaming(currentHost, currentPort + 1, Client.ClientId, args.ResponderId);
            UpdateChatForm(form => form.ShowActiveCallBanner(args.CallId, peerName));
        }
        else
        {
            UpdateChatForm(form => form.ShowComposerError("La llamada fue rechazada."));
        }
    }

    private async void HandleEndCallRequested(object? sender, EndCallRequestedEventArgs args)
    {
        var client = Client;
        if (client is null || !client.IsConnected) return;

        try
        {
            await client.SendCallEndAsync(args.TargetId, args.CallId);
            if (activeUdpStreamer is not null)
            {
                await activeUdpStreamer.DisposeAsync();
                activeUdpStreamer = null;
            }
        }
        catch { }
    }

    private async void HandleCallEnded(object? sender, CallEndEventArgs args)
    {
        if (activeUdpStreamer is not null)
        {
            await activeUdpStreamer.DisposeAsync();
            activeUdpStreamer = null;
        }
        UpdateChatForm(form => form.HideActiveCallBanner());
    }

    private void RestoreHistoryForUsers(IEnumerable<ClientInfo> clients, ChatForm form)
    {
        foreach (var user in clients)
        {
            if (Client is not null && user.Id == Client.ClientId) continue;
            if (conversationHistories.TryGetValue(user.Username, out var messages))
            {
                var existingIds = form.GetExistingMessageIds(user.Id);
                foreach (var msg in messages)
                {
                    if (!existingIds.Contains(msg.Id))
                    {
                        form.AppendMessage(user.Id, msg);
                    }
                }
            }
        }
    }

    private void RecordMessageInMemoryAndDisk(byte peerId, ChatMessageView message)
    {
        var peerName = Client?.Clients.FirstOrDefault(c => c.Id == peerId)?.Username;
        if (string.IsNullOrEmpty(peerName)) return;

        if (!conversationHistories.TryGetValue(peerName, out var list))
        {
            list = [];
            conversationHistories[peerName] = list;
        }

        if (!list.Any(m => m.Id == message.Id))
        {
            list.Add(message);
            PersistHistory();
        }
    }

    private void UpdateLocalHistoryEdited(byte peerId, string messageId, string newText)
    {
        var peerName = Client?.Clients.FirstOrDefault(c => c.Id == peerId)?.Username;
        if (string.IsNullOrEmpty(peerName) || !conversationHistories.TryGetValue(peerName, out var list)) return;

        var index = list.FindIndex(m => m.Id == messageId);
        if (index >= 0)
        {
            list[index] = list[index] with { Text = newText, IsEdited = true };
            PersistHistory();
        }
    }

    private void UpdateLocalHistoryDeleted(byte peerId, string messageId)
    {
        var peerName = Client?.Clients.FirstOrDefault(c => c.Id == peerId)?.Username;
        if (string.IsNullOrEmpty(peerName) || !conversationHistories.TryGetValue(peerName, out var list)) return;

        var index = list.FindIndex(m => m.Id == messageId);
        if (index >= 0)
        {
            list[index] = list[index] with { IsDeleted = true };
            PersistHistory();
        }
    }

    private void PersistHistory()
    {
        if (string.IsNullOrEmpty(currentUsername)) return;

        var export = conversationHistories.Select(kvp => new StoredConversation(
            kvp.Key,
            kvp.Value.Select(m => new StoredMessage(m.Id, m.Sender, m.Text, m.Timestamp, m.IsOwn, m.IsEdited, m.IsDeleted)).ToList()
        )).ToList();

        historyService.SaveHistory(currentUsername, export);
    }
}

public sealed record FileSendFailure(string FilePath, string Error);
