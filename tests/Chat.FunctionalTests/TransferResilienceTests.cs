using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Chat.Protocol;
using ChatCliente.Network;
using ChatServidor.Network;
using Xunit;

namespace Chat.FunctionalTests;

public sealed class TransferResilienceTests
{
    [Fact(Timeout = 10_000)]
    public async Task Interrupted_replace_preserves_original_and_removes_staging()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        var destination = Path.Combine(downloads, "report.txt");
        await File.WriteAllTextAsync(destination, "original");
        var resolver = new SequenceConflictResolver(FileConflictDecision.Replace);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads, resolver);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var transferId = Guid.NewGuid();
            var chunkReceived = WaitForIncomingProgress(receiver, transferId, 1);
            await SendFileStartAsync(rawStream, receiver.ClientId, transferId, "report.txt", 8);
            await SendFileChunkAsync(rawStream, receiver.ClientId, transferId, "new-data"u8.ToArray());
            await chunkReceived.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            await SendFileAbortAsync(rawStream, receiver.ClientId, transferId);
            await WaitUntilAsync(() => GetStagingFiles(downloads).Length == 0);

            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            Assert.True(receiver.IsConnected);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Successful_replace_changes_destination_only_after_file_end()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        var destination = Path.Combine(downloads, "report.txt");
        await File.WriteAllTextAsync(destination, "original");
        var resolver = new SequenceConflictResolver(FileConflictDecision.Replace);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads, resolver);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var transferId = Guid.NewGuid();
            var bytes = "replacement"u8.ToArray();
            var chunkReceived = WaitForIncomingProgress(receiver, transferId, 1);
            var completed = WaitForNextFile(receiver);
            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                transferId,
                "report.txt",
                bytes.Length);
            await SendFileChunkAsync(rawStream, receiver.ClientId, transferId, bytes);
            await chunkReceived.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            await SendFileEndAsync(rawStream, receiver.ClientId, transferId);
            var received = await completed.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(destination, received.FilePath);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            Assert.Empty(GetStagingFiles(downloads));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Same_name_replace_and_keep_both_complete_without_disconnect_or_overlap()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        await File.WriteAllTextAsync(Path.Combine(downloads, "shared.bin"), "old");
        var resolver = new SequenceConflictResolver(
            FileConflictDecision.Replace,
            FileConflictDecision.KeepBoth);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads, resolver);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var firstBytes = Enumerable.Repeat((byte)0x31, 40_000).ToArray();
            var secondBytes = Enumerable.Repeat((byte)0x72, 45_000).ToArray();
            var received = Channel.CreateUnbounded<FileReceivedEventArgs>();
            receiver.FileReceived += (_, args) => received.Writer.TryWrite(args);

            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                firstId,
                "shared.bin",
                firstBytes.Length);
            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                secondId,
                "shared.bin",
                secondBytes.Length);
            await SendBytesAsync(rawStream, receiver.ClientId, firstId, firstBytes);
            await SendBytesAsync(rawStream, receiver.ClientId, secondId, secondBytes);
            await SendFileEndAsync(rawStream, receiver.ClientId, firstId);
            await SendFileEndAsync(rawStream, receiver.ClientId, secondId);

            var first = await received.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
            var second = await received.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
            var paths = new[] { first.FilePath, second.FilePath };
            var contents = new[]
            {
                await File.ReadAllBytesAsync(paths[0]),
                await File.ReadAllBytesAsync(paths[1])
            };

            Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains(contents, bytes => bytes.SequenceEqual(firstBytes));
            Assert.Contains(contents, bytes => bytes.SequenceEqual(secondBytes));
            Assert.True(receiver.IsConnected);
            Assert.Empty(GetStagingFiles(downloads));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Conflict_created_after_file_start_is_resolved_at_commit()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        var resolver = new SequenceConflictResolver(FileConflictDecision.KeepBoth);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads, resolver);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var transferId = Guid.NewGuid();
            var bytes = "incoming"u8.ToArray();
            var chunkReceived = WaitForIncomingProgress(receiver, transferId, 1);
            var completed = WaitForNextFile(receiver);
            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                transferId,
                "late.txt",
                bytes.Length);
            await SendFileChunkAsync(rawStream, receiver.ClientId, transferId, bytes);
            await chunkReceived.WaitAsync(TimeSpan.FromSeconds(2));
            var lateDestination = Path.Combine(downloads, "late.txt");
            await File.WriteAllTextAsync(lateDestination, "created-late");

            await SendFileEndAsync(rawStream, receiver.ClientId, transferId);
            var received = await completed.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("created-late", await File.ReadAllTextAsync(lateDestination));
            Assert.Equal("late (1).txt", received.FileName);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(received.FilePath));
            Assert.True(receiver.IsConnected);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Two_same_name_replaces_finalize_sequentially_without_disconnect()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        var destination = Path.Combine(downloads, "shared.bin");
        await File.WriteAllTextAsync(destination, "old");
        var resolver = new ControlledConflictResolver(2);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads, resolver);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var firstBytes = Enumerable.Repeat((byte)0x11, 20_000).ToArray();
            var secondBytes = Enumerable.Repeat((byte)0x22, 21_000).ToArray();
            var received = Channel.CreateUnbounded<FileReceivedEventArgs>();
            receiver.FileReceived += (_, args) => received.Writer.TryWrite(args);
            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                firstId,
                "shared.bin",
                firstBytes.Length);
            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                secondId,
                "shared.bin",
                secondBytes.Length);
            await SendFileChunkAsync(rawStream, receiver.ClientId, firstId, firstBytes);
            await SendFileChunkAsync(rawStream, receiver.ClientId, secondId, secondBytes);
            await SendFileEndAsync(rawStream, receiver.ClientId, firstId);
            await SendFileEndAsync(rawStream, receiver.ClientId, secondId);
            await resolver.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            resolver.Release(0, FileConflictDecision.Replace);
            var first = await received.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(firstId, first.TransferId);
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(destination));

            resolver.Release(1, FileConflictDecision.Replace);
            var second = await received.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(secondId, second.TransferId);
            Assert.Equal(secondBytes, await File.ReadAllBytesAsync(destination));
            Assert.True(receiver.IsConnected);
            Assert.Empty(GetStagingFiles(downloads));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Sender_failure_after_start_aborts_only_that_transfer_and_connection_stays_usable()
    {
        using var sandbox = new TemporaryDirectory();
        var failedPath = Path.Combine(sandbox.Path, "failed.bin");
        var successfulPath = Path.Combine(sandbox.Path, "successful.bin");
        await File.WriteAllBytesAsync(failedPath, Enumerable.Repeat((byte)0x41, 100_000).ToArray());
        var successfulBytes = Enumerable.Repeat((byte)0x62, 41_000).ToArray();
        await File.WriteAllBytesAsync(successfulPath, successfulBytes);
        var downloads = Path.Combine(sandbox.Path, "downloads");
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = new ChatClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = new ChatClient(downloads);
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var twoChunksReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileProgressChanged += (_, args) =>
        {
            if (!args.IsOutgoing
                && args.FileName == "failed.bin"
                && args.Percentage >= 65)
            {
                twoChunksReceived.TrySetResult();
            }
        };
        sender.BeforeFileChunkSendAsync = async (_, chunkIndex, _) =>
        {
            if (chunkIndex != 2)
            {
                return;
            }

            await twoChunksReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            sender.BeforeFileChunkSendAsync = null;
            throw new InjectedTransferException();
        };
        var messageReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MessageReceived += (_, args) => messageReceived.TrySetResult(args.Text);
        var receivedFile = WaitForNextFile(receiver);

        await Assert.ThrowsAsync<InjectedTransferException>(
            () => sender.SendFileAsync(receiver.ClientId, failedPath));
        await WaitUntilAsync(
            () => GetStagingFiles(downloads).Length == 0
                && !File.Exists(Path.Combine(downloads, "failed.bin")));
        await sender.SendMessageAsync(receiver.ClientId, "after-abort");
        await sender.SendFileAsync(receiver.ClientId, successfulPath);
        var completed = await receivedFile.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            "after-abort",
            await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("successful.bin", completed.FileName);
        Assert.Equal(successfulBytes, await File.ReadAllBytesAsync(completed.FilePath));
        Assert.False(File.Exists(Path.Combine(downloads, "failed.bin")));
        Assert.True(sender.IsConnected);
        Assert.True(receiver.IsConnected);
    }

    [Fact(Timeout = 10_000)]
    public async Task Failed_abort_send_closes_sender_and_receiver_removes_abandoned_staging()
    {
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "failed-abort.bin");
        await File.WriteAllBytesAsync(
            sourcePath,
            Enumerable.Repeat((byte)0x41, 100_000).ToArray());
        var downloads = Path.Combine(sandbox.Path, "downloads");
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = new ChatClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = new ChatClient(downloads);
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var twoChunksReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileProgressChanged += (_, args) =>
        {
            if (!args.IsOutgoing
                && args.FileName == "failed-abort.bin"
                && args.Percentage >= 65)
            {
                twoChunksReceived.TrySetResult();
            }
        };
        sender.BeforeFileChunkSendAsync = async (_, chunkIndex, _) =>
        {
            if (chunkIndex != 2)
            {
                return;
            }

            await twoChunksReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InjectedTransferException();
        };
        var abortAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sender.TrySendFileAbortAsyncOverride = (_, _) =>
        {
            abortAttempted.TrySetResult();
            return Task.FromResult(false);
        };
        var senderDisconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Disconnected += (_, _) => senderDisconnected.TrySetResult();
        var senderRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ClientListChanged += (_, args) =>
        {
            if (args.Clients.All(client => client.Username != "Sender"))
            {
                senderRemoved.TrySetResult();
            }
        };
        var receivedCount = 0;
        receiver.FileReceived += (_, _) => Interlocked.Increment(ref receivedCount);

        await Assert.ThrowsAsync<InjectedTransferException>(
            () => sender.SendFileAsync(receiver.ClientId, sourcePath));
        await abortAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await senderDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await senderRemoved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => GetStagingFiles(downloads).Length == 0);
        await Task.Delay(50);

        Assert.Equal(0, Volatile.Read(ref receivedCount));
        Assert.False(sender.IsConnected);
        Assert.True(receiver.IsConnected);
        Assert.False(File.Exists(Path.Combine(downloads, "failed-abort.bin")));
    }

    [Fact(Timeout = 10_000)]
    public async Task Source_open_failure_occurs_before_file_start()
    {
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "locked.bin");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        var downloads = Path.Combine(sandbox.Path, "downloads");
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = new ChatClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = new ChatClient(downloads);
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        await using var lockStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        await Assert.ThrowsAsync<IOException>(
            () => sender.SendFileAsync(receiver.ClientId, sourcePath));
        await Task.Delay(50);

        Assert.False(Directory.Exists(downloads)
            && Directory.EnumerateFiles(downloads).Any());
        Assert.True(sender.IsConnected);
        Assert.True(receiver.IsConnected);
    }

    [Fact(Timeout = 10_000)]
    public async Task Pending_conflict_decision_does_not_block_message_or_unrelated_file()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        await File.WriteAllTextAsync(Path.Combine(downloads, "blocked.txt"), "old");
        var resolver = new PendingConflictResolver();
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads, resolver);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var blockedId = Guid.NewGuid();
            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                blockedId,
                "blocked.txt",
                1);
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var messageReceived = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.MessageReceived += (_, args) => messageReceived.TrySetResult(args.Text);
            var otherReceived = WaitForNextFile(receiver);
            var otherId = Guid.NewGuid();

            await FrameCodec.WriteAsync(
                rawStream,
                new Frame(
                    FrameCommand.TextMessage,
                    receiver.ClientId,
                    Encoding.UTF8.GetBytes("while-deciding")));
            await SendFileStartAsync(rawStream, receiver.ClientId, otherId, "other.bin", 1);
            await SendFileChunkAsync(rawStream, receiver.ClientId, otherId, [0x5A]);
            await SendFileEndAsync(rawStream, receiver.ClientId, otherId);

            Assert.Equal(
                "while-deciding",
                await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            var completed = await otherReceived.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("other.bin", completed.FileName);
            Assert.Equal([0x5A], await File.ReadAllBytesAsync(completed.FilePath));

            await SendFileAbortAsync(rawStream, receiver.ClientId, blockedId);
            await resolver.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => GetStagingFiles(downloads).Length == 0);
            Assert.True(receiver.IsConnected);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Disconnect_cancels_pending_conflict_and_deletes_staging()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        Directory.CreateDirectory(downloads);
        await File.WriteAllTextAsync(Path.Combine(downloads, "blocked.txt"), "old");
        var resolver = new PendingConflictResolver();
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads, resolver);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                Guid.NewGuid(),
                "blocked.txt",
                10);
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await receiver.DisconnectAsync();

            await resolver.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(receiver.IsConnected);
            Assert.Empty(GetStagingFiles(downloads));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task AbortConnection_during_chunk_raises_disconnected_after_receive_cleanup()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var transferId = Guid.NewGuid();
            var abortIssued = 0;
            var disconnectedFlag = 0;
            var disconnectedCount = 0;
            var lateProgressCount = 0;
            var fileReceivedCount = 0;
            var errorCount = 0;
            var disconnected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileProgressChanged += (_, args) =>
            {
                if (!args.IsOutgoing
                    && args.TransferId == transferId
                    && args.Percentage > 0
                    && Interlocked.Exchange(ref abortIssued, 1) == 0)
                {
                    receiver.AbortConnection();
                }
            };
            receiver.FileProgressChanged += (_, args) =>
            {
                if (args.TransferId == transferId
                    && Volatile.Read(ref disconnectedFlag) != 0)
                {
                    Interlocked.Increment(ref lateProgressCount);
                }
            };
            receiver.FileReceived += (_, _) => Interlocked.Increment(ref fileReceivedCount);
            receiver.ErrorReceived += (_, _) => Interlocked.Increment(ref errorCount);
            receiver.Disconnected += (_, _) =>
            {
                Interlocked.Exchange(ref disconnectedFlag, 1);
                Interlocked.Increment(ref disconnectedCount);
                disconnected.TrySetResult();
            };
            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                transferId,
                "abort-during-chunk.bin",
                80_000);
            await SendFileChunkAsync(
                rawStream,
                receiver.ClientId,
                transferId,
                Enumerable.Repeat((byte)0x33, FileChunkPayload.MaximumChunkLength).ToArray());

            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => GetStagingFiles(downloads).Length == 0);
            await Task.Delay(25);

            Assert.Equal(1, Volatile.Read(ref disconnectedCount));
            Assert.Equal(0, Volatile.Read(ref lateProgressCount));
            Assert.Equal(0, Volatile.Read(ref fileReceivedCount));
            Assert.Equal(0, Volatile.Read(ref errorCount));
            Assert.False(receiver.IsConnected);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task AbortConnection_during_finalization_drains_before_disconnected_event()
    {
        using var sandbox = new TemporaryDirectory();
        var downloads = Path.Combine(sandbox.Path, "downloads");
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(downloads);
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var finalizationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finalizationCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.BeforeIncomingFileCommitAsync = async (_, cancellationToken) =>
        {
            finalizationStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                finalizationCancelled.TrySetResult();
                throw;
            }
        };
        var (rawClient, rawStream, _) = await RegisterRawClientAsync(server.ActualPort, "Sender");
        using (rawClient)
        await using (rawStream)
        {
            var transferId = Guid.NewGuid();
            var disconnectedCount = 0;
            var fileReceivedCount = 0;
            var progressAfterDisconnected = 0;
            var disconnectedFlag = 0;
            var disconnected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.Disconnected += (_, _) =>
            {
                Interlocked.Exchange(ref disconnectedFlag, 1);
                Interlocked.Increment(ref disconnectedCount);
                disconnected.TrySetResult();
            };
            receiver.FileReceived += (_, _) => Interlocked.Increment(ref fileReceivedCount);
            receiver.FileProgressChanged += (_, _) =>
            {
                if (Volatile.Read(ref disconnectedFlag) != 0)
                {
                    Interlocked.Increment(ref progressAfterDisconnected);
                }
            };
            var bytes = Enumerable.Repeat((byte)0x55, 20_000).ToArray();
            await SendFileStartAsync(
                rawStream,
                receiver.ClientId,
                transferId,
                "abort-finalizing.bin",
                bytes.Length);
            await SendFileChunkAsync(rawStream, receiver.ClientId, transferId, bytes);
            await SendFileEndAsync(rawStream, receiver.ClientId, transferId);
            await finalizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            receiver.AbortConnection();

            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(finalizationCancelled.Task.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref disconnectedCount));
            Assert.Equal(0, Volatile.Read(ref fileReceivedCount));
            Assert.Equal(0, Volatile.Read(ref progressAfterDisconnected));
            Assert.Empty(GetStagingFiles(downloads));
            Assert.False(File.Exists(Path.Combine(downloads, "abort-finalizing.bin")));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Immediate_reconnect_waits_for_previous_receive_loop_cleanup()
    {
        using var sandbox = new TemporaryDirectory();
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var peer = new ChatClient(Path.Combine(sandbox.Path, "peer"));
        await using var reconnecting = new ChatClient(Path.Combine(sandbox.Path, "reconnecting"));
        await peer.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Peer");
        await reconnecting.ConnectAndRegisterAsync(
            "127.0.0.1",
            server.ActualPort,
            "OriginalSession");
        var oldCleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        reconnecting.BeforeReceiveLoopCleanupAsync = async (_, _) =>
        {
            reconnecting.BeforeReceiveLoopCleanupAsync = null;
            oldCleanupStarted.TrySetResult();
            await releaseOldCleanup.Task;
        };

        reconnecting.AbortConnection();
        await oldCleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reconnectTask = reconnecting.ConnectAndRegisterAsync(
            "127.0.0.1",
            server.ActualPort,
            "ReconnectedSession");

        await Assert.ThrowsAsync<TimeoutException>(
            () => reconnectTask.WaitAsync(TimeSpan.FromMilliseconds(150)));
        Assert.False(reconnecting.IsConnected);

        releaseOldCleanup.TrySetResult();
        var result = await reconnectTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Accepted);
        Assert.True(reconnecting.IsConnected);
        var routed = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MessageReceived += (_, args) => routed.TrySetResult(args.Text);

        await reconnecting.SendMessageAsync(peer.ClientId, "new-session-is-alive");

        Assert.Equal(
            "new-session-is-alive",
            await routed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(peer.IsConnected);
    }

    [Fact(Timeout = 10_000)]
    public async Task Receive_loop_waits_for_accepted_session_publication_before_processing_frames()
    {
        using var sandbox = new TemporaryDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var bufferedSession = ServeBufferedAcceptedSessionAsync(listener);
        await using var client = new ChatClient(Path.Combine(sandbox.Path, "client"));
        var publishPaused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeAcceptedSessionPublishAsync = async (_, _) =>
        {
            client.BeforeAcceptedSessionPublishAsync = null;
            publishPaused.TrySetResult();
            await releasePublication.Task;
        };
        var clientListObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ClientListChanged += (_, args) =>
        {
            if (args.Clients.Any(peer => peer.Username == "BufferedPeer"))
            {
                clientListObserved.TrySetResult();
            }
        };
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        var connectTask = client.ConnectAndRegisterAsync(
            "127.0.0.1",
            port,
            "BufferedClient");
        await publishPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await bufferedSession.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TimeoutException>(
            () => clientListObserved.Task.WaitAsync(TimeSpan.FromMilliseconds(150)));
        await Assert.ThrowsAsync<TimeoutException>(
            () => disconnected.Task.WaitAsync(TimeSpan.FromMilliseconds(150)));
        Assert.False(client.IsConnected);

        releasePublication.TrySetResult();
        var accepted = await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(accepted.Accepted);
        await clientListObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(client.IsConnected);

        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var peer = new ChatClient(Path.Combine(sandbox.Path, "peer"));
        await peer.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Peer");
        var reconnected = await client.ConnectAndRegisterAsync(
            "127.0.0.1",
            server.ActualPort,
            "ReconnectedClient");
        Assert.True(reconnected.Accepted);
        var routed = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MessageReceived += (_, args) => routed.TrySetResult(args.Text);

        await client.SendMessageAsync(peer.ClientId, "published-session-is-routable");

        Assert.Equal(
            "published-session-is-routable",
            await routed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(client.IsConnected);
    }

    [Fact(Timeout = 10_000)]
    public async Task Throwing_disconnected_subscriber_does_not_block_reconnect()
    {
        using var sandbox = new TemporaryDirectory();
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var peer = new ChatClient(Path.Combine(sandbox.Path, "peer"));
        await using var client = new ChatClient(Path.Combine(sandbox.Path, "client"));
        await peer.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Peer");
        await client.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "OriginalClient");
        var healthyDisconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, _) => throw new InjectedSubscriberException();
        client.Disconnected += (_, _) => healthyDisconnected.TrySetResult();

        client.AbortConnection();

        await healthyDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reconnected = await client.ConnectAndRegisterAsync(
            "127.0.0.1",
            server.ActualPort,
            "ReconnectedClient");
        Assert.True(reconnected.Accepted);
        var routed = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MessageReceived += (_, args) => routed.TrySetResult(args.Text);

        await client.SendMessageAsync(peer.ClientId, "subscriber-failure-contained");

        Assert.Equal(
            "subscriber-failure-contained",
            await routed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(client.IsConnected);
        Assert.True(peer.IsConnected);
    }

    [Fact(Timeout = 10_000)]
    public async Task Throwing_public_event_subscribers_do_not_block_healthy_subscribers()
    {
        using var sandbox = new TemporaryDirectory();
        var sourcePath = Path.Combine(sandbox.Path, "event-isolation.bin");
        var expectedBytes = Enumerable.Repeat((byte)0x5A, 40_000).ToArray();
        await File.WriteAllBytesAsync(sourcePath, expectedBytes);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var receiver = new ChatClient(Path.Combine(sandbox.Path, "receiver"));
        await using var sender = new ChatClient(Path.Combine(sandbox.Path, "sender"));
        var fullListObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var messageObserved = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progressObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fileObserved = new TaskCompletionSource<FileReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var errorObserved = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.ClientListChanged += (_, _) => throw new InjectedSubscriberException();
        receiver.ClientListChanged += (_, args) =>
        {
            if (args.Clients.Count == 2)
            {
                fullListObserved.TrySetResult();
            }
        };
        receiver.MessageReceived += (_, _) => throw new InjectedSubscriberException();
        receiver.MessageReceived += (_, args) => messageObserved.TrySetResult(args.Text);
        receiver.FileProgressChanged += (_, _) => throw new InjectedSubscriberException();
        receiver.FileProgressChanged += (_, args) =>
        {
            if (!args.IsOutgoing
                && args.FileName == "event-isolation.bin"
                && args.Percentage > 0)
            {
                progressObserved.TrySetResult();
            }
        };
        receiver.FileReceived += (_, _) => throw new InjectedSubscriberException();
        receiver.FileReceived += (_, args) => fileObserved.TrySetResult(args);
        sender.ErrorReceived += (_, _) => throw new InjectedSubscriberException();
        sender.ErrorReceived += (_, args) => errorObserved.TrySetResult(args.Message);

        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await fullListObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await sender.SendMessageAsync(receiver.ClientId, "healthy-message-handler");
        await sender.SendFileAsync(receiver.ClientId, sourcePath);
        await sender.SendMessageAsync(byte.MaxValue, "missing-target");

        Assert.Equal(
            "healthy-message-handler",
            await messageObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await progressObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var completed = await fileObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(completed.FilePath));
        Assert.Equal(
            ProtocolMessages.MissingTarget,
            await errorObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(sender.IsConnected);
        Assert.True(receiver.IsConnected);
    }

    [Fact(Timeout = 10_000)]
    public async Task Aborted_generation_cannot_emit_outgoing_progress_after_reconnect()
    {
        using var sandbox = new TemporaryDirectory();
        var oldPath = Path.Combine(sandbox.Path, "old-generation.bin");
        var newPath = Path.Combine(sandbox.Path, "new-generation.bin");
        await File.WriteAllBytesAsync(
            oldPath,
            Enumerable.Repeat((byte)0x31, 40_000).ToArray());
        var newBytes = Enumerable.Repeat((byte)0x72, 45_000).ToArray();
        await File.WriteAllBytesAsync(newPath, newBytes);
        await using var server = new ChatServer(0);
        await server.StartAsync();
        await using var sender = new ChatClient(Path.Combine(sandbox.Path, "sender"));
        await using var receiver = new ChatClient(Path.Combine(sandbox.Path, "receiver"));
        await sender.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Sender");
        await receiver.ConnectAndRegisterAsync("127.0.0.1", server.ActualPort, "Receiver");
        var chunkWritten = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProgress = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sender.AfterFileChunkSendBeforeProgressAsync = async (_, chunkIndex, _) =>
        {
            if (chunkIndex != 0)
            {
                return;
            }

            sender.AfterFileChunkSendBeforeProgressAsync = null;
            chunkWritten.TrySetResult();
            await releaseProgress.Task;
        };
        var disconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedCount = 0;
        sender.Disconnected += (_, _) =>
        {
            Interlocked.Increment(ref disconnectedCount);
            disconnected.TrySetResult();
        };
        var oldProgressCount = 0;
        var newProgressCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sender.FileProgressChanged += (_, args) =>
        {
            if (args.IsOutgoing && args.FileName == "old-generation.bin")
            {
                Interlocked.Increment(ref oldProgressCount);
            }

            if (args.IsOutgoing
                && args.FileName == "new-generation.bin"
                && args.Percentage == 100)
            {
                newProgressCompleted.TrySetResult();
            }
        };
        var newFileReceived = new TaskCompletionSource<FileReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileReceived += (_, args) =>
        {
            if (args.FileName == "new-generation.bin")
            {
                newFileReceived.TrySetResult(args);
            }
        };

        var oldSend = sender.SendFileAsync(receiver.ClientId, oldPath);
        await chunkWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        sender.AbortConnection();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reconnect = await sender.ConnectAndRegisterAsync(
            "127.0.0.1",
            server.ActualPort,
            "ReconnectedSender");
        Assert.True(reconnect.Accepted);

        releaseProgress.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => oldSend.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, Volatile.Read(ref oldProgressCount));
        Assert.True(sender.IsConnected);

        await sender.SendFileAsync(receiver.ClientId, newPath);
        await newProgressCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var completed = await newFileReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(newBytes, await File.ReadAllBytesAsync(completed.FilePath));
        Assert.Equal(1, Volatile.Read(ref disconnectedCount));
        Assert.True(sender.IsConnected);
        Assert.True(receiver.IsConnected);
    }

    [Fact(Timeout = 10_000)]
    public async Task AbortConnection_without_receive_loop_is_idempotent_and_silent()
    {
        using var sandbox = new TemporaryDirectory();
        await using var client = new ChatClient(Path.Combine(sandbox.Path, "downloads"));
        var disconnectedCount = 0;
        client.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);

        client.AbortConnection();
        client.AbortConnection();
        await client.DisconnectAsync();

        Assert.Equal(0, Volatile.Read(ref disconnectedCount));
        Assert.False(client.IsConnected);
        Assert.Equal((byte)0, client.ClientId);
    }

    private static Task WaitForIncomingProgress(
        ChatClient client,
        Guid transferId,
        int minimumPercentage)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<FileProgressEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (!args.IsOutgoing
                && args.TransferId == transferId
                && args.Percentage >= minimumPercentage)
            {
                client.FileProgressChanged -= handler;
                signal.TrySetResult();
            }
        };
        client.FileProgressChanged += handler;
        return signal.Task;
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

    private static Task SendFileStartAsync(
        Stream stream,
        byte targetId,
        Guid transferId,
        string fileName,
        long length)
    {
        return FrameCodec.WriteAsync(
            stream,
            new Frame(
                FrameCommand.FileStart,
                targetId,
                JsonPayload.Serialize(new FileStartPayload(transferId, fileName, length))))
            .AsTask();
    }

    private static Task SendFileChunkAsync(
        Stream stream,
        byte targetId,
        Guid transferId,
        byte[] bytes)
    {
        return FrameCodec.WriteAsync(
            stream,
            new Frame(
                FrameCommand.FileChunk,
                targetId,
                FileChunkPayload.Create(transferId, bytes)))
            .AsTask();
    }

    private static async Task SendBytesAsync(
        Stream stream,
        byte targetId,
        Guid transferId,
        byte[] bytes)
    {
        foreach (var chunk in bytes.Chunk(FileChunkPayload.MaximumChunkLength))
        {
            await SendFileChunkAsync(stream, targetId, transferId, chunk);
        }
    }

    private static Task SendFileEndAsync(Stream stream, byte targetId, Guid transferId)
    {
        return FrameCodec.WriteAsync(
            stream,
            new Frame(
                FrameCommand.FileEnd,
                targetId,
                JsonPayload.Serialize(new FileEndPayload(transferId))))
            .AsTask();
    }

    private static Task SendFileAbortAsync(Stream stream, byte targetId, Guid transferId)
    {
        return FrameCodec.WriteAsync(
            stream,
            new Frame(
                FrameCommand.FileAbort,
                targetId,
                JsonPayload.Serialize(new FileAbortPayload(transferId))))
            .AsTask();
    }

    private static string[] GetStagingFiles(string downloads)
    {
        return Directory.Exists(downloads)
            ? Directory.GetFiles(downloads, ".chat-transfer-*.tmp")
            : [];
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeoutAt)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The expected asynchronous state was not reached.");
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

    private static async Task ServeBufferedAcceptedSessionAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var registration = await FrameCodec.ReadAsync(stream);
        Assert.NotNull(registration);
        Assert.Equal(FrameCommand.Register, registration.Command);
        await FrameCodec.WriteAsync(
            stream,
            new Frame(
                FrameCommand.RegistrationResult,
                0,
                JsonPayload.Serialize(
                    new RegistrationResultPayload(true, 7, null))));
        await FrameCodec.WriteAsync(
            stream,
            new Frame(
                FrameCommand.ClientList,
                0,
                JsonPayload.Serialize(
                    new ClientListPayload(
                    [
                        new ClientInfo(7, "BufferedClient"),
                        new ClientInfo(8, "BufferedPeer")
                    ]))));
        await FrameCodec.WriteAsync(
            stream,
            new Frame(FrameCommand.Disconnect, 0, []));
    }

    private sealed class SequenceConflictResolver(params FileConflictDecision[] decisions)
        : IFileConflictResolver
    {
        private int index;

        public ValueTask<FileConflictDecision> ResolveAsync(
            FileConflictContext conflict,
            CancellationToken cancellationToken = default)
        {
            var selected = decisions[Math.Min(
                Interlocked.Increment(ref index) - 1,
                decisions.Length - 1)];
            return ValueTask.FromResult(selected);
        }
    }

    private sealed class PendingConflictResolver : IFileConflictResolver
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<FileConflictDecision> ResolveAsync(
            FileConflictContext conflict,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return FileConflictDecision.KeepBoth;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ControlledConflictResolver(int count) : IFileConflictResolver
    {
        private readonly TaskCompletionSource<FileConflictDecision>[] decisions =
            Enumerable.Range(0, count)
                .Select(_ => new TaskCompletionSource<FileConflictDecision>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
        private int started;

        public TaskCompletionSource AllStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<FileConflictDecision> ResolveAsync(
            FileConflictContext conflict,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref started) - 1;
            if (index + 1 == decisions.Length)
            {
                AllStarted.TrySetResult();
            }

            return await decisions[index].Task.WaitAsync(cancellationToken);
        }

        public void Release(int index, FileConflictDecision decision)
        {
            decisions[index].TrySetResult(decision);
        }
    }

    private sealed class InjectedTransferException : Exception;

    private sealed class InjectedSubscriberException : Exception;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"chat-resilience-{Guid.NewGuid():N}");
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
