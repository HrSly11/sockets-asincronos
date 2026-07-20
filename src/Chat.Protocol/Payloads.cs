using System.Text.Json;

namespace Chat.Protocol;

public sealed record RegisterPayload(string Username);

public sealed record RegistrationResultPayload(bool Accepted, byte ClientId, string? Error);

public sealed record ClientInfo(byte Id, string Username);

public sealed record ClientListPayload(IReadOnlyList<ClientInfo> Clients);

public sealed record FileStartPayload(Guid TransferId, string FileName, long Length);

public sealed record FileEndPayload(Guid TransferId);

public sealed record FileAbortPayload(Guid TransferId);

public sealed record ErrorPayload(string Message);

public sealed record TextMessagePayload(string MessageId, string Text);

public sealed record EditMessagePayload(string MessageId, string NewText);

public sealed record DeleteMessagePayload(string MessageId);

public sealed record CreateGroupPayload(string GroupName, IReadOnlyList<byte> MemberIds);

public sealed record GroupCreatedPayload(Guid GroupId, string GroupName, IReadOnlyList<ClientInfo> Members);

public sealed record GroupMessagePayload(Guid GroupId, string MessageId, string Text);


public static class JsonPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize<T>(T payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.SerializeToUtf8Bytes(payload, Options);
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize<T>(payload, Options)
            ?? throw new InvalidDataException($"The {typeof(T).Name} JSON payload was empty.");
    }
}

public static class FileChunkPayload
{
    public const int TransferIdLength = 16;
    public const int MaximumChunkLength = 32 * 1024;

    public static byte[] Create(Guid transferId, ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length > MaximumChunkLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunk),
                $"A file chunk cannot exceed {MaximumChunkLength} bytes.");
        }

        var payload = new byte[TransferIdLength + chunk.Length];
        transferId.TryWriteBytes(payload);
        chunk.CopyTo(payload.AsSpan(TransferIdLength));
        return payload;
    }

    public static (Guid TransferId, ReadOnlyMemory<byte> Chunk) Parse(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < TransferIdLength || payload.Length > TransferIdLength + MaximumChunkLength)
        {
            throw new InvalidDataException("The file chunk payload has an invalid length.");
        }

        return (new Guid(payload.AsSpan(0, TransferIdLength)), payload.AsMemory(TransferIdLength));
    }
}

public static class UsernameValidator
{
    public const int MaximumLength = 20;

    public static bool IsValid(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > MaximumLength)
        {
            return false;
        }

        return username.All(character =>
            char.IsLetterOrDigit(character)
            || character is ' ' or '_' or '-');
    }
}
