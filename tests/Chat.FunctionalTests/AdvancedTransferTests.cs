using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Windows.Forms;
using Chat.Protocol;
using ChatCliente;
using ChatCliente.Application;
using ChatCliente.Network;
using ChatServidor.Network;
using Guna.UI2.WinForms;
using Xunit;

namespace Chat.FunctionalTests;

public sealed class AdvancedTransferTests
{
    [Fact(Timeout = 10_000)]
    public async Task Gated_large_transfer_allows_message_delivery_before_file_completion()
    {
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "large.bin");
        await File.WriteAllBytesAsync(
            sourcePath,
            Enumerable.Range(0, 70_000).Select(value => (byte)(value % 251)).ToArray());
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = NewClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = NewClient(Path.Combine(sandbox.Path, "receiver"));
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var twoChunksReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedChunkCount = 0;
        receiver.FileProgressChanged += (_, args) =>
        {
            if (!args.IsOutgoing
                && args.FileName == "large.bin"
                && args.Percentage > 0
                && Interlocked.Increment(ref receivedChunkCount) >= 2)
            {
                twoChunksReceived.TrySetResult();
            }
        };
        var activeTransferBlocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFile = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sender.BeforeFileChunkSendAsync = async (_, chunkIndex, _) =>
        {
            if (chunkIndex != 2)
            {
                return;
            }

            await twoChunksReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            activeTransferBlocked.TrySetResult();
            await releaseFile.Task;
        };
        var messageReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fileReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MessageReceived += (_, args) => messageReceived.TrySetResult(args.Text);
        receiver.FileReceived += (_, _) => fileReceived.TrySetResult();

        var fileTask = sender.SendFileAsync(receiver.ClientId, sourcePath);
        await activeTransferBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sender.SendMessageAsync(receiver.ClientId, "message-before-file-end");

        Assert.Equal(
            "message-before-file-end",
            await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(fileReceived.Task.IsCompleted);
        releaseFile.TrySetResult();
        await fileTask;
        await fileReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact(Timeout = 10_000)]
    public async Task Three_simultaneous_files_preserve_bytes_progress_and_concurrent_message()
    {
        using var sandbox = new TemporaryDirectory();
        var expectedByName = new Dictionary<string, byte[]>();
        foreach (var (name, length, seed) in new[]
        {
            ("alpha.bin", 70_013, 3),
            ("beta.bin", 98_417, 7),
            ("gamma.bin", 131_111, 11)
        })
        {
            var bytes = Enumerable.Range(0, length)
                .Select(value => (byte)((value + seed) % 251))
                .ToArray();
            expectedByName[name] = bytes;
            await File.WriteAllBytesAsync(Path.Combine(sandbox.Path, name), bytes);
        }

        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = NewClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = NewClient(Path.Combine(sandbox.Path, "receiver"));
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var gatedTransfers = new ConcurrentDictionary<Guid, byte>();
        var allTransfersGated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFiles = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var incomingChunkCounts = new ConcurrentDictionary<Guid, int>();
        var twoChunksReceived = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        receiver.FileProgressChanged += (_, args) =>
        {
            if (args.IsOutgoing || args.Percentage == 0)
            {
                return;
            }

            var count = incomingChunkCounts.AddOrUpdate(
                args.TransferId,
                1,
                (_, current) => current + 1);
            if (count >= 2
                && twoChunksReceived.TryGetValue(args.TransferId, out var received))
            {
                received.TrySetResult();
            }
        };
        sender.BeforeFileChunkSendAsync = async (transferId, chunkIndex, _) =>
        {
            if (chunkIndex != 2)
            {
                return;
            }

            var received = twoChunksReceived.GetOrAdd(
                transferId,
                _ => new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            if (incomingChunkCounts.GetValueOrDefault(transferId) >= 2)
            {
                received.TrySetResult();
            }

            await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            gatedTransfers.TryAdd(transferId, 0);
            if (gatedTransfers.Count == 3)
            {
                allTransfersGated.TrySetResult();
            }

            await releaseFiles.Task;
        };
        var progressByTransfer = new ConcurrentDictionary<Guid, int>();
        sender.FileProgressChanged += (_, args) =>
        {
            if (args.IsOutgoing)
            {
                progressByTransfer[args.TransferId] = args.Percentage;
            }
        };
        var receivedFiles = Channel.CreateUnbounded<FileReceivedEventArgs>();
        receiver.FileReceived += (_, args) => receivedFiles.Writer.TryWrite(args);
        var messageReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MessageReceived += (_, args) => messageReceived.TrySetResult(args.Text);

        var sendTasks = expectedByName.Keys
            .Select(name => sender.SendFileAsync(receiver.ClientId, Path.Combine(sandbox.Path, name)))
            .ToArray();
        await allTransfersGated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await sender.SendMessageAsync(receiver.ClientId, "concurrent-message");
        Assert.Equal(
            "concurrent-message",
            await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.All(sendTasks, task => Assert.False(task.IsCompleted));

        releaseFiles.TrySetResult();
        var transferIds = await Task.WhenAll(sendTasks);
        var completed = new List<FileReceivedEventArgs>();
        while (completed.Count < 3)
        {
            completed.Add(
                await receivedFiles.Reader.ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(3)));
        }

        Assert.Equal(3, completed.Select(file => file.TransferId).Distinct().Count());
        foreach (var file in completed)
        {
            Assert.Equal(expectedByName[file.FileName], await File.ReadAllBytesAsync(file.FilePath));
        }

        Assert.All(transferIds, transferId => Assert.Equal(100, progressByTransfer[transferId]));
    }

    [Fact(Timeout = 10_000)]
    public Task Attachment_picker_contract_carries_every_selected_path()
    {
        return RunInStaAsync(
            () =>
            {
                var selected = new[] { @"C:\one.txt", @"C:\two.txt", @"C:\three.txt" };
                var picker = new FakeFileSelectionService(selected);
                using var form = new ChatForm("Me", picker);
                form.Show();
                form.SetConnectionState(true, "Conectado");
                form.SetUsers([new ChatUserView(2, "Peer", true)]);
                form.SelectRecipient(2);
                AttachmentRequestedEventArgs? captured = null;
                form.AttachmentRequested += (_, args) => captured = args;

                FindButton(form, "Adjuntar archivo").PerformClick();

                Assert.NotNull(captured);
                Assert.Equal(2, captured.TargetId);
                Assert.Equal(selected, captured.FilePaths);
                Assert.Equal(1, picker.InvocationCount);
                return Task.CompletedTask;
            });
    }

    [Fact(Timeout = 10_000)]
    public async Task Selected_batch_is_concurrent_keeps_composer_enabled_and_isolates_failures()
    {
        var fakeClient = new BatchFakeChatClient();
        using var login = new LoginForm();
        await using var coordinator = new ClientCoordinator(login, _ => fakeClient);
        Assert.True(await coordinator.ConnectAsync("Me", "127.0.0.1:55000"));
        var form = Assert.IsType<ChatForm>(coordinator.ChatForm);
        Assert.True(form.SelectRecipient(2));
        var paths = new[] { "ok-one.bin", "broken.bin", "ok-two.bin" };

        var batchTask = coordinator.SendFilesAsync(2, paths);
        await fakeClient.AllFileSendsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(form.IsComposerEnabled);
        form.SetMessageDraft("still chatting");
        FindButton(form, "Enviar mensaje").PerformClick();
        Assert.Equal(
            "still chatting",
            await fakeClient.MessageSent.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(batchTask.IsCompleted);

        fakeClient.ReleaseSuccessfulFiles.TrySetResult();
        var failures = await batchTask;
        Assert.Collection(
            failures,
            failure =>
            {
                Assert.Equal("broken.bin", failure.FilePath);
                Assert.Contains("broken", failure.Error);
            });
        Assert.Equal(["ok-one.bin", "ok-two.bin"], fakeClient.SuccessfulFiles.Order());
        Assert.Contains("still chatting", form.VisibleMessages.Select(message => message.Text));
    }

    [Fact(Timeout = 10_000)]
    public async Task Collision_replace_overwrites_the_requested_path_after_callback()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        var requestedPath = Path.Combine(downloads, "report.txt");
        await File.WriteAllTextAsync(requestedPath, "old");
        var resolver = new RecordingConflictResolver(FileConflictDecision.Replace);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads, resolver);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var received = WaitForNextFile(receiver);
            await SendRawFileAsync(rawStream, receiver.ClientId, "report.txt", "new"u8.ToArray());
            var completed = await received.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(requestedPath, completed.FilePath);
            Assert.Equal("new", await File.ReadAllTextAsync(requestedPath));
            var conflict = Assert.Single(resolver.Conflicts);
            Assert.Equal(requestedPath, conflict.RequestedPath);
            Assert.Equal("report.txt", conflict.FileName);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Collision_keep_both_uses_first_available_number_without_overwriting()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        var original = Path.Combine(downloads, "photo.png");
        await File.WriteAllTextAsync(original, "original");
        var resolver = new RecordingConflictResolver(FileConflictDecision.KeepBoth);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads, resolver);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var firstReceived = WaitForNextFile(receiver);
            await SendRawFileAsync(rawStream, receiver.ClientId, "photo.png", [1]);
            var first = await firstReceived.WaitAsync(TimeSpan.FromSeconds(3));
            var secondReceived = WaitForNextFile(receiver);
            await SendRawFileAsync(rawStream, receiver.ClientId, "photo.png", [2]);
            var second = await secondReceived.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal("original", await File.ReadAllTextAsync(original));
            Assert.Equal("photo (1).png", first.FileName);
            Assert.Equal([1], await File.ReadAllBytesAsync(first.FilePath));
            Assert.Equal("photo (2).png", second.FileName);
            Assert.Equal([2], await File.ReadAllBytesAsync(second.FilePath));
            Assert.Equal(2, resolver.Conflicts.Count);
        }
    }

    [Fact(Timeout = 10_000)]
    public Task Ui_conflict_resolution_completes_asynchronously_without_deadlock()
    {
        return RunInStaAsync(
            async () =>
            {
                using var owner = new Form();
                owner.Show();
                var resolver = new UiFileConflictResolver(() => owner);
                var resolution = Task.Run(
                    async () => await resolver.ResolveAsync(
                        new FileConflictContext(
                            "report.txt",
                            @"C:\downloads\report.txt",
                            2,
                            Guid.NewGuid())));

                Form? dialog = null;
                var timeoutAt = DateTime.UtcNow.AddSeconds(2);
                while (dialog is null && DateTime.UtcNow < timeoutAt)
                {
                    System.Windows.Forms.Application.DoEvents();
                    dialog = owner.OwnedForms
                        .FirstOrDefault(candidate => candidate.Text == "Archivo existente");
                    await Task.Delay(1);
                }

                Assert.NotNull(dialog);
                FindButton(dialog, "Conservar ambos").PerformClick();
                Assert.Equal(
                    FileConflictDecision.KeepBoth,
                    await resolution.WaitAsync(TimeSpan.FromSeconds(2)));
            });
    }

    [Fact(Timeout = 10_000)]
    public async Task Unicode_chat_filename_and_content_round_trip_exactly()
    {
        using var sandbox = new TemporaryDirectory();
        const string message = "¡Hola, José! 😀 漢字 العربية עברית";
        const string fileName = "résumé-😀-漢字-العربية.txt";
        var content = Encoding.UTF8.GetBytes("áéíóú — 😀 — 日本語 — مرحباً");
        var sourcePath = Path.Combine(sandbox.Path, fileName);
        await File.WriteAllBytesAsync(sourcePath, content);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = NewClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = NewClient(Path.Combine(sandbox.Path, "receiver"));
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "José");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "李雷");
        var messageReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fileReceived = new TaskCompletionSource<FileReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MessageReceived += (_, args) => messageReceived.TrySetResult(args.Text);
        receiver.FileReceived += (_, args) => fileReceived.TrySetResult(args);

        await sender.SendMessageAsync(receiver.ClientId, message);
        await sender.SendFileAsync(receiver.ClientId, sourcePath);

        Assert.Equal(
            message,
            await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var completed = await fileReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(fileName, completed.FileName);
        Assert.Equal(content, await File.ReadAllBytesAsync(completed.FilePath));
    }

    private static ChatClient NewClient(string downloadsDirectory)
    {
        return new ChatClient(downloadsDirectory);
    }

    private static Task<FileReceivedEventArgs> WaitForNextFile(ChatClient client)
    {
        var signal = new TaskCompletionSource<FileReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<FileReceivedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            client.FileReceived -= handler;
            signal.TrySetResult(args);
        };
        client.FileReceived += handler;
        return signal.Task;
    }

    private static async Task SendRawFileAsync(
        Stream stream,
        byte targetId,
        string fileName,
        byte[] bytes)
    {
        var transferId = Guid.NewGuid();
        await FrameCodec.WriteAsync(
            stream,
            new Frame(
                FrameCommand.FileStart,
                targetId,
                JsonPayload.Serialize(new FileStartPayload(transferId, fileName, bytes.Length))));
        if (bytes.Length > 0)
        {
            await FrameCodec.WriteAsync(
                stream,
                new Frame(
                    FrameCommand.FileChunk,
                    targetId,
                    FileChunkPayload.Create(transferId, bytes)));
        }

        await FrameCodec.WriteAsync(
            stream,
            new Frame(
                FrameCommand.FileEnd,
                targetId,
                JsonPayload.Serialize(new FileEndPayload(transferId))));
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
        var result = JsonPayload.Deserialize<RegistrationResultPayload>(response.Payload);
        Assert.True(result.Accepted);
        return (client, stream, result.ClientId);
    }

    private static Task RunInStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    var task = action();
                    while (!task.IsCompleted)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(1);
                    }

                    task.GetAwaiter().GetResult();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static Guna2Button FindButton(Control root, string accessibleName)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Guna2Button button && button.AccessibleName == accessibleName)
            {
                return button;
            }

            try
            {
                return FindButton(child, accessibleName);
            }
            catch (InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException($"Button '{accessibleName}' was not found.");
    }

    private sealed class FakeFileSelectionService(IReadOnlyList<string> paths)
        : IFileSelectionService
    {
        public int InvocationCount { get; private set; }

        public IReadOnlyList<string> SelectFiles(IWin32Window owner)
        {
            InvocationCount++;
            return paths;
        }
    }

    private sealed class RecordingConflictResolver(FileConflictDecision decision)
        : IFileConflictResolver
    {
        public List<FileConflictContext> Conflicts { get; } = [];

        public ValueTask<FileConflictDecision> ResolveAsync(
            FileConflictContext conflict,
            CancellationToken cancellationToken = default)
        {
            Conflicts.Add(conflict);
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class BatchFakeChatClient : IChatClient
    {
        private readonly ConcurrentDictionary<string, byte> startedFiles = [];
        private readonly ConcurrentBag<string> successfulFiles = [];

        public TaskCompletionSource AllFileSendsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSuccessfulFiles { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> MessageSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> SuccessfulFiles => successfulFiles.ToArray();

        public event EventHandler<ClientListChangedEventArgs>? ClientListChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<TextMessageReceivedEventArgs>? MessageReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<FileProgressEventArgs>? FileProgressChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<FileReceivedEventArgs>? FileReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<ClientErrorEventArgs>? ErrorReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<MessageEditedEventArgs>? MessageEdited
        {
            add { }
            remove { }
        }

        public event EventHandler<MessageDeletedEventArgs>? MessageDeleted
        {
            add { }
            remove { }
        }

        public event EventHandler? Disconnected
        {
            add { }
            remove { }
        }

        public byte ClientId { get; private set; }

        public bool IsConnected { get; private set; }

        public IReadOnlyList<ClientInfo> Clients { get; } =
            [new ClientInfo(1, "Me"), new ClientInfo(2, "Peer")];

        public Task<RegistrationResultPayload> ConnectAndRegisterAsync(
            string host,
            int port,
            string username,
            CancellationToken cancellationToken = default)
        {
            ClientId = 1;
            IsConnected = true;
            return Task.FromResult(new RegistrationResultPayload(true, 1, null));
        }

        public Task SendMessageAsync(
            byte targetId,
            string text,
            CancellationToken cancellationToken = default)
        {
            return SendMessageAsync(targetId, Guid.NewGuid().ToString("N"), text, cancellationToken);
        }

        public Task SendMessageAsync(
            byte targetId,
            string messageId,
            string text,
            CancellationToken cancellationToken = default)
        {
            MessageSent.TrySetResult(text);
            return Task.CompletedTask;
        }

        public Task SendEditMessageAsync(
            byte targetId,
            string messageId,
            string newText,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendDeleteMessageAsync(
            byte targetId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<Guid> SendFileAsync(
            byte targetId,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            startedFiles.TryAdd(filePath, 0);
            if (startedFiles.Count == 3)
            {
                AllFileSendsStarted.TrySetResult();
            }

            if (filePath == "broken.bin")
            {
                throw new IOException("broken file");
            }

            await ReleaseSuccessfulFiles.Task.WaitAsync(cancellationToken);
            successfulFiles.Add(filePath);
            return Guid.NewGuid();
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"chat-advanced-{Guid.NewGuid():N}");
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
