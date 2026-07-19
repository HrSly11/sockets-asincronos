namespace ChatCliente.Network;

public enum FileConflictDecision
{
    Replace,
    KeepBoth
}

public sealed record FileConflictContext(
    string FileName,
    string RequestedPath,
    byte SenderId,
    Guid TransferId);

public interface IFileConflictResolver
{
    ValueTask<FileConflictDecision> ResolveAsync(
        FileConflictContext conflict,
        CancellationToken cancellationToken = default);
}
