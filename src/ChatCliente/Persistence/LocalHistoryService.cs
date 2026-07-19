using System.Text.Json;

namespace ChatCliente.Persistence;

public sealed record StoredMessage(
    string Id,
    string Sender,
    string Text,
    DateTimeOffset Timestamp,
    bool IsOwn,
    bool IsEdited,
    bool IsDeleted);

public sealed record StoredConversation(
    string PeerUsername,
    List<StoredMessage> Messages);

public sealed class LocalHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string storageDirectory;

    public LocalHistoryService(string? customDirectory = null)
    {
        storageDirectory = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChatRedes",
            "History");
    }

    public List<StoredConversation> LoadHistory(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return [];
        }

        try
        {
            var filePath = GetFilePath(username);
            if (!File.Exists(filePath))
            {
                return [];
            }

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<StoredConversation>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveHistory(string username, IEnumerable<StoredConversation> history)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(storageDirectory);
            var filePath = GetFilePath(username);
            var json = JsonSerializer.Serialize(history, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch
        {
            // Ignore persistence errors gracefully
        }
    }

    private string GetFilePath(string username)
    {
        var sanitized = string.Concat(username.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(storageDirectory, $"history_{sanitized.ToLowerInvariant()}.json");
    }
}
