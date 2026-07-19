using System.Net;
using System.Net.Sockets;
using System.Text;
using Chat.Protocol;
using ChatCliente.Network;
using ChatServidor.Network;
using Xunit;

namespace Chat.FunctionalTests;

public sealed class NetworkIntegrationTests
{
    [Fact(Timeout = 10_000)]
    public async Task Server_and_two_clients_register_and_receive_the_full_client_list()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var ada = NewClient();
        await using var grace = NewClient();
        var adaSawBoth = SignalWhenClientCountIs(ada, 2);
        var graceSawBoth = SignalWhenClientCountIs(grace, 2);

        Assert.True((await ada.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Ada")).Accepted);
        Assert.True((await grace.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Grace")).Accepted);

        var adaClients = await adaSawBoth.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var graceClients = await graceSawBoth.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["Ada", "Grace"], adaClients.Select(client => client.Username).Order());
        Assert.Equal(["Ada", "Grace"], graceClients.Select(client => client.Username).Order());
        Assert.All(adaClients, client => Assert.InRange(client.Id, (byte)1, byte.MaxValue));
    }

    [Fact(Timeout = 10_000)]
    public async Task Duplicate_username_is_rejected_case_insensitively_with_the_exact_message()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var first = NewClient();
        await using var duplicate = NewClient();
        Assert.True((await first.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Ada")).Accepted);

        var result = await duplicate.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "aDa");

        Assert.False(result.Accepted);
        Assert.Equal("Ese nombre ya está en uso. Prueba con otro.", result.Error);
        Assert.False(duplicate.IsConnected);
    }

    [Fact(Timeout = 10_000)]
    public async Task Private_text_is_delivered_only_to_the_target_with_the_sender_id()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var ada = NewClient();
        await using var grace = NewClient();
        await using var linus = NewClient();
        await ada.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Ada");
        await grace.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Grace");
        await linus.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Linus");
        var receivedByGrace = new TaskCompletionSource<TextMessageReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedByLinus = new TaskCompletionSource<TextMessageReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        grace.MessageReceived += (_, args) => receivedByGrace.TrySetResult(args);
        linus.MessageReceived += (_, args) => receivedByLinus.TrySetResult(args);

        await ada.SendMessageAsync(grace.ClientId, "Hola Grace");

        var message = await receivedByGrace.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ada.ClientId, message.SenderId);
        Assert.Equal("Hola Grace", message.Text);
        await AssertQuietAsync(receivedByLinus.Task, TimeSpan.FromMilliseconds(250));
    }

    [Fact(Timeout = 10_000)]
    public async Task Abrupt_disconnect_removes_the_client_and_broadcasts_the_new_list()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var observer = NewClient();
        await using var departing = NewClient();
        await observer.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Observer");
        await departing.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Departing");
        await WaitForClientCountAsync(observer, 2);
        var sawOne = WaitForClientCountAsync(observer, 1);

        departing.AbortConnection();

        var remaining = await sawOne;
        Assert.Collection(remaining, client => Assert.Equal("Observer", client.Username));
    }

    [Fact(Timeout = 10_000)]
    public async Task Multi_chunk_file_is_preserved_exactly_and_progress_reaches_100()
    {
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "payload.bin");
        var expected = new byte[FileChunkPayload.MaximumChunkLength * 2 + 173];
        Random.Shared.NextBytes(expected);
        await File.WriteAllBytesAsync(sourcePath, expected);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = NewClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = NewClient(Path.Combine(sandbox.Path, "receiver"));
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var received = new TaskCompletionSource<FileReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileReceived += (_, args) => received.TrySetResult(args);
        var progress = new List<int>();
        sender.FileProgressChanged += (_, args) =>
        {
            if (args.IsOutgoing)
            {
                progress.Add(args.Percentage);
            }
        };

        await sender.SendFileAsync(receiver.ClientId, sourcePath);

        var completed = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sender.ClientId, completed.SenderId);
        Assert.Equal("payload.bin", completed.FileName);
        Assert.Equal(expected, await File.ReadAllBytesAsync(completed.FilePath));
        Assert.Contains(100, progress);
        Assert.True(progress.Count(value => value is > 0 and < 100) >= 2);
    }

    [Fact(Timeout = 10_000)]
    public async Task Zero_byte_file_is_created_and_reported_as_complete()
    {
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "empty.txt");
        await File.WriteAllBytesAsync(sourcePath, []);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = NewClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = NewClient(Path.Combine(sandbox.Path, "receiver"));
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var received = new TaskCompletionSource<FileReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileReceived += (_, args) => received.TrySetResult(args);

        await sender.SendFileAsync(receiver.ClientId, sourcePath);

        var completed = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, new FileInfo(completed.FilePath).Length);
        Assert.Equal("empty.txt", completed.FileName);
    }

    [Fact(Timeout = 10_000)]
    public async Task Missing_target_returns_an_error_and_server_keeps_serving_clients()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var client = NewClient();
        await client.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Ada");
        var error = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ErrorReceived += (_, args) => error.TrySetResult(args.Message);

        await client.SendMessageAsync(byte.MaxValue, "Anybody?");

        Assert.Equal(
            "El destinatario no está conectado.",
            await error.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(server.IsRunning);
        Assert.True(client.IsConnected);
    }

    [Fact(Timeout = 10_000)]
    public async Task Stop_completes_and_releases_the_bound_port()
    {
        var server = new ChatServer(0);
        await server.StartAsync();
        var port = server.ActualPort;

        await server.StopAsync();

        Assert.False(server.IsRunning);
        using var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start();
        Assert.Equal(port, ((IPEndPoint)probe.LocalEndpoint).Port);
        probe.Stop();
        await server.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task Three_client_smoke_routes_text_and_file_end_to_end()
    {
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "smoke.dat");
        var expected = Enumerable.Range(0, 40_000).Select(value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, expected);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = NewClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = NewClient(Path.Combine(sandbox.Path, "receiver"));
        await using var observer = NewClient(Path.Combine(sandbox.Path, "observer"));
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        await observer.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Observer");
        var textReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MessageReceived += (_, args) => textReceived.TrySetResult(args.Text);
        receiver.FileReceived += (_, args) => fileReceived.TrySetResult(args.FilePath);
        observer.MessageReceived += (_, _) => observerReceived.TrySetResult();

        await sender.SendMessageAsync(receiver.ClientId, "smoke-message");
        await sender.SendFileAsync(receiver.ClientId, sourcePath);

        Assert.Equal(
            "smoke-message",
            await textReceived.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var receivedPath = await fileReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, await File.ReadAllBytesAsync(receivedPath));
        await AssertQuietAsync(observerReceived.Task, TimeSpan.FromMilliseconds(250));
    }

    [Fact(Timeout = 10_000)]
    public async Task Recipient_disconnect_during_forward_returns_missing_target_without_dropping_sender()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = NewClient();
        await using var recipient = NewClient();
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await recipient.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Recipient");
        await WaitForClientCountAsync(sender, 2);
        var recipientId = recipient.ClientId;
        var listAfterDisconnect = WaitForClientCountAsync(sender, 1);
        var missingTarget = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var senderDisconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sender.ErrorReceived += (_, args) => missingTarget.TrySetResult(args.Message);
        sender.Disconnected += (_, _) => senderDisconnected.TrySetResult();
        server.BeforeRecipientSendAsync = async (targetId, _) =>
        {
            if (targetId != recipientId)
            {
                return;
            }

            server.BeforeRecipientSendAsync = null;
            recipient.AbortConnection();
            await listAfterDisconnect;
        };

        await sender.SendMessageAsync(recipientId, "race");

        Assert.Equal(
            ProtocolMessages.MissingTarget,
            await missingTarget.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(sender.IsConnected);
        await AssertQuietAsync(senderDisconnected.Task, TimeSpan.FromMilliseconds(250));

        await using var replacement = NewClient();
        await replacement.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Replacement");
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        replacement.MessageReceived += (_, args) => delivered.TrySetResult(args.Text);
        await sender.SendMessageAsync(replacement.ClientId, "still-connected");
        Assert.Equal(
            "still-connected",
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact(Timeout = 10_000)]
    public async Task Stale_session_finally_cannot_remove_a_replacement_that_reused_the_same_id()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = NewClient();
        await using var staleRecipient = NewClient();
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await staleRecipient.ConnectAndRegisterAsync(
            "127.0.0.1",
            server.ActualPort,
            "StaleRecipient");
        await WaitForClientCountAsync(sender, 2);
        var reusedId = staleRecipient.ClientId;
        var staleFinallyPaused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleFinally = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleFinallyCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeClientFinallyCleanupAsync = async (clientId, connectionKey, _) =>
        {
            if (clientId != reusedId)
            {
                return;
            }

            server.BeforeClientFinallyCleanupAsync = null;
            staleFinallyPaused.TrySetResult();
            await releaseStaleFinally.Task;
        };
        server.AfterClientFinallyCleanup = (clientId, _, removed) =>
        {
            if (clientId == reusedId)
            {
                staleFinallyCompleted.TrySetResult(removed);
            }
        };
        var sawCleanupFirst = WaitForClientCountAsync(sender, 1);
        server.BeforeRecipientSendAsync = async (targetId, _) =>
        {
            if (targetId != reusedId)
            {
                return;
            }

            server.BeforeRecipientSendAsync = null;
            staleRecipient.AbortConnection();
            await staleFinallyPaused.Task;
        };

        await sender.SendMessageAsync(reusedId, "trigger-cleanup");
        await sawCleanupFirst;
        await using var replacement = NewClient();
        await replacement.ConnectAndRegisterAsync(
            "127.0.0.1",
            server.ActualPort,
            "Replacement");
        Assert.Equal(reusedId, replacement.ClientId);
        await WaitForClientCountAsync(sender, 2);

        releaseStaleFinally.TrySetResult();
        Assert.False(
            await staleFinallyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains(
            sender.Clients,
            client => client.Id == replacement.ClientId
                && client.Username == "Replacement");
        var delivered = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        replacement.MessageReceived += (_, args) => delivered.TrySetResult(args.Text);
        await sender.SendMessageAsync(replacement.ClientId, "replacement-is-routable");
        Assert.Equal(
            "replacement-is-routable",
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact(Timeout = 10_000)]
    public async Task Departed_sender_id_is_retired_until_cleanup_snapshot_reaches_receivers()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = NewClient(downloads);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (departingClient, departingStream, departingId) =
            await RegisterRawClientAsync(server.ActualPort, "Departing");
        using (departingClient)
        await using (departingStream)
        {
            var transferId = Guid.NewGuid();
            var stagingCreated = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileProgressChanged += (_, args) =>
            {
                if (!args.IsOutgoing
                    && args.TransferId == transferId
                    && args.Percentage > 0)
                {
                    stagingCreated.TrySetResult();
                }
            };
            await FrameCodec.WriteAsync(
                departingStream,
                new Frame(
                    FrameCommand.FileStart,
                    receiver.ClientId,
                    JsonPayload.Serialize(
                        new FileStartPayload(transferId, "abandoned.bin", 10))));
            await FrameCodec.WriteAsync(
                departingStream,
                new Frame(
                    FrameCommand.FileChunk,
                    receiver.ClientId,
                    FileChunkPayload.Create(transferId, [1, 2, 3])));
            await stagingCreated.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var retirementReadyForRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRetiredId = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeRetiredClientIdReleaseAsync = async (clientId, _, _) =>
            {
                if (clientId != departingId)
                {
                    return;
                }

                server.BeforeRetiredClientIdReleaseAsync = null;
                retirementReadyForRelease.TrySetResult();
                await releaseRetiredId.Task;
            };

            departingClient.Close();
            await retirementReadyForRelease.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => !Directory.Exists(downloads)
                    || !Directory.EnumerateFiles(downloads, ".chat-transfer-*.tmp").Any());
            Assert.DoesNotContain(receiver.Clients, client => client.Id == departingId);

            await using var pressureReplacement = NewClient();
            var pressureResult = await pressureReplacement.ConnectAndRegisterAsync(
                "127.0.0.1",
                server.ActualPort,
                "PressureReplacement");
            Assert.True(pressureResult.Accepted);
            Assert.NotEqual(departingId, pressureReplacement.ClientId);

            releaseRetiredId.TrySetResult();
            await pressureReplacement.DisconnectAsync();
            await WaitForClientCountAsync(receiver, 1);

            await using var reusedReplacement = NewClient();
            var reusedResult = await reusedReplacement.ConnectAndRegisterAsync(
                "127.0.0.1",
                server.ActualPort,
                "ReusedReplacement");
            Assert.True(reusedResult.Accepted);
            Assert.Equal(departingId, reusedReplacement.ClientId);
            var routed = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            reusedReplacement.MessageReceived += (_, args) => routed.TrySetResult(args.Text);

            await receiver.SendMessageAsync(reusedReplacement.ClientId, "retirement-complete");

            Assert.Equal(
                "retirement-complete",
                await routed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(receiver.IsConnected);
            Assert.True(reusedReplacement.IsConnected);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Concurrent_duplicate_registrations_accept_exactly_one_client()
    {
        await using var server = new ChatServer(0);
        await server.StartAsync();
        var clients = Enumerable.Range(0, 8).Select(_ => NewClient()).ToArray();
        try
        {
            var results = await Task.WhenAll(clients.Select(
                client => client.ConnectAndRegisterAsync(
                    "127.0.0.1",
                    server.ActualPort,
                    "Same_Name")));

            Assert.Single(results, result => result.Accepted);
            Assert.Equal(7, results.Count(
                result => !result.Accepted
                    && result.Error == ProtocolMessages.DuplicateUsername));
        }
        finally
        {
            await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask()));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Stop_with_active_clients_disconnects_everyone_and_releases_the_port()
    {
        var server = new ChatServer(0);
        await server.StartAsync();
        var port = server.ActualPort;
        await using var first = NewClient();
        await using var second = NewClient();
        var firstDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Disconnected += (_, _) => firstDisconnected.TrySetResult();
        second.Disconnected += (_, _) => secondDisconnected.TrySetResult();
        await first.ConnectAndRegisterAsync("127.0.0.1", port, "First");
        await second.ConnectAndRegisterAsync("127.0.0.1", port, "Second");

        await server.StopAsync();

        await Task.WhenAll(
            firstDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(3)),
            secondDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(first.IsConnected);
        Assert.False(second.IsConnected);
        using var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start();
        probe.Stop();
        await server.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task Hostile_filename_is_sanitized_and_collision_does_not_overwrite()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        var existingPath = Path.Combine(downloads, "evil_.txt");
        await File.WriteAllTextAsync(existingPath, "existing");
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(
            downloads,
            new KeepBothConflictResolver());
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "RawSender");
        using (rawClient)
        await using (rawStream)
        {
            var transferId = Guid.NewGuid();
            var bytes = "safe payload"u8.ToArray();
            var received = new TaskCompletionSource<FileReceivedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileReceived += (_, args) => received.TrySetResult(args);

            await FrameCodec.WriteAsync(
                rawStream,
                new Frame(
                    FrameCommand.FileStart,
                    receiver.ClientId,
                    JsonPayload.Serialize(
                        new FileStartPayload(transferId, @"..\..\evil?.txt", bytes.Length))));
            await FrameCodec.WriteAsync(
                rawStream,
                new Frame(
                    FrameCommand.FileChunk,
                    receiver.ClientId,
                    FileChunkPayload.Create(transferId, bytes)));
            await FrameCodec.WriteAsync(
                rawStream,
                new Frame(
                    FrameCommand.FileEnd,
                    receiver.ClientId,
                    JsonPayload.Serialize(new FileEndPayload(transferId))));

            var completed = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("existing", await File.ReadAllTextAsync(existingPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.FilePath));
            Assert.Equal(downloads, Path.GetDirectoryName(completed.FilePath));
            Assert.DoesNotContain("..", completed.FileName);
            Assert.DoesNotContain('?', completed.FileName);
            Assert.NotEqual(existingPath, completed.FilePath);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Incomplete_transfer_deletes_staging_and_keeps_connection_usable()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = NewClient(downloads);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "RawSender");
        using (rawClient)
        await using (rawStream)
        {
            var transferId = Guid.NewGuid();
            var error = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.ErrorReceived += (_, args) => error.TrySetResult(args.Message);
            await FrameCodec.WriteAsync(
                rawStream,
                new Frame(
                    FrameCommand.FileStart,
                    receiver.ClientId,
                    JsonPayload.Serialize(new FileStartPayload(transferId, "partial.bin", 10))));
            await FrameCodec.WriteAsync(
                rawStream,
                new Frame(
                    FrameCommand.FileChunk,
                    receiver.ClientId,
                    FileChunkPayload.Create(transferId, [1, 2, 3])));
            await FrameCodec.WriteAsync(
                rawStream,
                new Frame(
                    FrameCommand.FileEnd,
                    receiver.ClientId,
                    JsonPayload.Serialize(new FileEndPayload(transferId))));

            Assert.Contains(
                "incomplete",
                await error.Task.WaitAsync(TimeSpan.FromSeconds(3)),
                StringComparison.OrdinalIgnoreCase);
            await WaitUntilAsync(
                () => !Directory.Exists(downloads)
                    || !Directory.EnumerateFiles(downloads).Any());
            Assert.Empty(Directory.Exists(downloads)
                ? Directory.EnumerateFiles(downloads)
                : []);
            var message = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.MessageReceived += (_, args) => message.TrySetResult(args.Text);
            await FrameCodec.WriteAsync(
                rawStream,
                new Frame(
                    FrameCommand.TextMessage,
                    receiver.ClientId,
                    Encoding.UTF8.GetBytes("after-incomplete")));

            Assert.Equal(
                "after-incomplete",
                await message.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(receiver.IsConnected);
        }
    }

    private static ChatClient NewClient(string? downloadsDirectory = null)
    {
        return new ChatClient(downloadsDirectory ?? Path.Combine(Path.GetTempPath(), $"chat-test-{Guid.NewGuid():N}"));
    }

    private sealed class KeepBothConflictResolver : IFileConflictResolver
    {
        public ValueTask<FileConflictDecision> ResolveAsync(
            FileConflictContext conflict,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(FileConflictDecision.KeepBoth);
        }
    }

    private static TaskCompletionSource<IReadOnlyList<ClientInfo>> SignalWhenClientCountIs(
        ChatClient client,
        int count)
    {
        var signal = new TaskCompletionSource<IReadOnlyList<ClientInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ClientListChanged += (_, args) =>
        {
            if (args.Clients.Count == count)
            {
                signal.TrySetResult(args.Clients);
            }
        };
        return signal;
    }

    private static async Task<IReadOnlyList<ClientInfo>> WaitForClientCountAsync(
        ChatClient client,
        int count)
    {
        if (client.Clients.Count == count)
        {
            return client.Clients;
        }

        var signal = SignalWhenClientCountIs(client, count);
        if (client.Clients.Count == count)
        {
            signal.TrySetResult(client.Clients);
        }

        return await signal.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task AssertQuietAsync(Task task, TimeSpan quietWindow)
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => task.WaitAsync(quietWindow));
    }

    private static async Task<(TcpClient Client, NetworkStream Stream, byte ClientId)>
        RegisterRawClientAsync(int port, string username)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await FrameCodec.WriteAsync(
            stream,
            new Frame(
                FrameCommand.Register,
                0,
                JsonPayload.Serialize(new RegisterPayload(username))));
        var response = await FrameCodec.ReadAsync(stream);
        Assert.NotNull(response);
        Assert.Equal(FrameCommand.RegistrationResult, response.Command);
        var result = JsonPayload.Deserialize<RegistrationResultPayload>(response.Payload);
        Assert.True(result.Accepted);
        return (client, stream, result.ClientId);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chat-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
