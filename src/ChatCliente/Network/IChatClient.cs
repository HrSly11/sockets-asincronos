using Chat.Protocol;

namespace ChatCliente.Network;

public interface IChatClient : IAsyncDisposable
{
    event EventHandler<ClientListChangedEventArgs>? ClientListChanged;

    event EventHandler<TextMessageReceivedEventArgs>? MessageReceived;

    event EventHandler<FileProgressEventArgs>? FileProgressChanged;

    event EventHandler<FileReceivedEventArgs>? FileReceived;

    event EventHandler<ClientErrorEventArgs>? ErrorReceived;

    event EventHandler<MessageEditedEventArgs>? MessageEdited;

    event EventHandler<MessageDeletedEventArgs>? MessageDeleted;

    event EventHandler<GroupCreatedEventArgs>? GroupCreated;

    event EventHandler<GroupMessageReceivedEventArgs>? GroupMessageReceived;

    event EventHandler? Disconnected;

    byte ClientId { get; }

    bool IsConnected { get; }

    IReadOnlyList<ClientInfo> Clients { get; }

    Task<RegistrationResultPayload> ConnectAndRegisterAsync(
        string host,
        int port,
        string username,
        CancellationToken cancellationToken = default);

    Task SendMessageAsync(
        byte targetId,
        string text,
        CancellationToken cancellationToken = default);

    Task SendMessageAsync(
        byte targetId,
        string messageId,
        string text,
        CancellationToken cancellationToken = default);

    Task CreateGroupAsync(
        string groupName,
        IReadOnlyList<byte> memberIds,
        CancellationToken cancellationToken = default);

    Task SendGroupMessageAsync(
        Guid groupId,
        string messageId,
        string text,
        CancellationToken cancellationToken = default);

    Task SendEditMessageAsync(
        byte targetId,
        string messageId,
        string newText,
        CancellationToken cancellationToken = default);

    Task SendDeleteMessageAsync(
        byte targetId,
        string messageId,
        CancellationToken cancellationToken = default);

    Task<Guid> SendFileAsync(
        byte targetId,
        string filePath,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed class MessageEditedEventArgs(byte senderId, string messageId, string newText) : EventArgs
{
    public byte SenderId { get; } = senderId;

    public string MessageId { get; } = messageId;

    public string NewText { get; } = newText;
}

public sealed class MessageDeletedEventArgs(byte senderId, string messageId) : EventArgs
{
    public byte SenderId { get; } = senderId;

    public string MessageId { get; } = messageId;
}

