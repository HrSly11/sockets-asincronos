namespace ChatCliente;

public sealed record ChatUserView(byte Id, string DisplayName, bool IsOnline);

public sealed record ChatMessageView(
    string Id,
    string Sender,
    string Text,
    DateTimeOffset Timestamp,
    bool IsOwn,
    bool IsEdited = false,
    bool IsDeleted = false);

public sealed record FileTransferView(
    string Id,
    string FileName,
    int Progress,
    string StatusText,
    bool IsOwn);

public sealed class LoginRequestedEventArgs(string userName, string serverAddress) : EventArgs
{
    public string UserName { get; } = userName;

    public string ServerAddress { get; } = serverAddress;
}

public sealed class MessageRequestedEventArgs(byte targetId, string message) : EventArgs
{
    public byte TargetId { get; } = targetId;

    public string Message { get; } = message;
}

public sealed class EditMessageRequestedEventArgs(byte targetId, string messageId, string newText) : EventArgs
{
    public byte TargetId { get; } = targetId;

    public string MessageId { get; } = messageId;

    public string NewText { get; } = newText;
}

public sealed class DeleteMessageRequestedEventArgs(byte targetId, string messageId) : EventArgs
{
    public byte TargetId { get; } = targetId;

    public string MessageId { get; } = messageId;
}

public sealed class AttachmentRequestedEventArgs(
    byte targetId,
    IReadOnlyList<string> filePaths) : EventArgs
{
    public byte TargetId { get; } = targetId;

    public IReadOnlyList<string> FilePaths { get; } = filePaths;
}

public sealed record ChatGroupView(Guid Id, string GroupName, IReadOnlyList<string> MemberNames);

public sealed class SendGroupMessageRequestedEventArgs(Guid groupId, string message) : EventArgs
{
    public Guid GroupId { get; } = groupId;

    public string Message { get; } = message;
}

public sealed record VoiceNoteView(string VoiceNoteId, string Sender, string DurationText, byte[] AudioData, bool IsOwn);

public sealed class SendVoiceNoteRequestedEventArgs(byte targetId, byte[] audioData, long durationMs) : EventArgs
{
    public byte TargetId { get; } = targetId;
    public byte[] AudioData { get; } = audioData;
    public long DurationMs { get; } = durationMs;
}

public sealed class CallRequestedEventArgs(byte targetId) : EventArgs
{
    public byte TargetId { get; } = targetId;
}

public sealed class CallAnsweredRequestedEventArgs(byte targetId, Guid callId, bool accepted) : EventArgs
{
    public byte TargetId { get; } = targetId;
    public Guid CallId { get; } = callId;
    public bool Accepted { get; } = accepted;
}

public sealed class EndCallRequestedEventArgs(byte targetId, Guid callId) : EventArgs
{
    public byte TargetId { get; } = targetId;
    public Guid CallId { get; } = callId;
}
