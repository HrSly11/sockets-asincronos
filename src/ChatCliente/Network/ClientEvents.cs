using Chat.Protocol;

namespace ChatCliente.Network;

public sealed class ClientListChangedEventArgs(IReadOnlyList<ClientInfo> clients) : EventArgs
{
    public IReadOnlyList<ClientInfo> Clients { get; } = clients;
}

public sealed class TextMessageReceivedEventArgs(byte senderId, string messageId, string text) : EventArgs
{
    public byte SenderId { get; } = senderId;

    public string MessageId { get; } = messageId;

    public string Text { get; } = text;
}

public sealed class FileProgressEventArgs(
    Guid transferId,
    byte peerId,
    string fileName,
    int percentage,
    bool isOutgoing) : EventArgs
{
    public Guid TransferId { get; } = transferId;

    public byte PeerId { get; } = peerId;

    public string FileName { get; } = fileName;

    public int Percentage { get; } = percentage;

    public bool IsOutgoing { get; } = isOutgoing;
}

public sealed class FileReceivedEventArgs(
    Guid transferId,
    byte senderId,
    string fileName,
    string filePath) : EventArgs
{
    public Guid TransferId { get; } = transferId;

    public byte SenderId { get; } = senderId;

    public string FileName { get; } = fileName;

    public string FilePath { get; } = filePath;
}

public sealed class ClientErrorEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class GroupCreatedEventArgs(Guid groupId, string groupName, IReadOnlyList<ClientInfo> members) : EventArgs
{
    public Guid GroupId { get; } = groupId;
    public string GroupName { get; } = groupName;
    public IReadOnlyList<ClientInfo> Members { get; } = members;
}

public sealed class GroupMessageReceivedEventArgs(Guid groupId, byte senderId, string messageId, string text) : EventArgs
{
    public Guid GroupId { get; } = groupId;
    public byte SenderId { get; } = senderId;
    public string MessageId { get; } = messageId;
    public string Text { get; } = text;
}
