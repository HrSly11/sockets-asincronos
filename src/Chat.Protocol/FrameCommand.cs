namespace Chat.Protocol;

public enum FrameCommand : byte
{
    TextMessage = 1,
    FileChunk = 2,
    FileEnd = 3,
    ClientList = 4,
    Disconnect = 5,
    Register = 6,
    RegistrationResult = 7,
    FileStart = 8,
    Error = 9,
    FileAbort = 10,
    EditMessage = 11,
    DeleteMessage = 12
}
