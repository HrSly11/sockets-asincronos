using Chat.Protocol;
using ChatCliente.Network;
using ChatServidor.Network;
using Xunit;

namespace Chat.FunctionalTests;

public sealed class GroupChatTests
{
    [Fact]
    public async Task CreateGroup_And_BroadcastGroupMessage_WorksAcrossThreeClients()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();
        var port = server.ActualPort;

        var tempDir = Path.GetTempPath();
        await using var clientA = new ChatClient(tempDir);
        await using var clientB = new ChatClient(tempDir);
        await using var clientC = new ChatClient(tempDir);

        await clientA.ConnectAndRegisterAsync("127.0.0.1", port, "UserA");
        await clientB.ConnectAndRegisterAsync("127.0.0.1", port, "UserB");
        await clientC.ConnectAndRegisterAsync("127.0.0.1", port, "UserC");

        await Task.Delay(200);

        var groupCreatedB = new TaskCompletionSource<GroupCreatedEventArgs>();
        var groupCreatedC = new TaskCompletionSource<GroupCreatedEventArgs>();

        clientB.GroupCreated += (_, e) => groupCreatedB.TrySetResult(e);
        clientC.GroupCreated += (_, e) => groupCreatedC.TrySetResult(e);

        // Client A creates group "Estudio" with B and C
        await clientA.CreateGroupAsync("Estudio", [clientB.ClientId, clientC.ClientId]);

        var eventB = await groupCreatedB.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var eventC = await groupCreatedC.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("Estudio", eventB.GroupName);
        Assert.Equal("Estudio", eventC.GroupName);
        Assert.Equal(3, eventB.Members.Count);

        var groupMsgB = new TaskCompletionSource<GroupMessageReceivedEventArgs>();
        var groupMsgC = new TaskCompletionSource<GroupMessageReceivedEventArgs>();

        clientB.GroupMessageReceived += (_, e) => groupMsgB.TrySetResult(e);
        clientC.GroupMessageReceived += (_, e) => groupMsgC.TrySetResult(e);

        var msgId = Guid.NewGuid().ToString("N");
        await clientA.SendGroupMessageAsync(eventB.GroupId, msgId, "Hola a todos en el grupo!");

        var receivedB = await groupMsgB.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var receivedC = await groupMsgC.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("Hola a todos en el grupo!", receivedB.Text);
        Assert.Equal("Hola a todos en el grupo!", receivedC.Text);
        Assert.Equal(clientA.ClientId, receivedB.SenderId);
        Assert.Equal(clientA.ClientId, receivedC.SenderId);
    }
}
