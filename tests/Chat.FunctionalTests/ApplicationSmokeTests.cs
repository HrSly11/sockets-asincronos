using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using ChatCliente;
using ChatCliente.Application;
using ChatCliente.Network;
using ChatServidor;
using ChatServidor.Application;
using ChatServidor.Network;
using Guna.UI2.WinForms;
using Xunit;

namespace Chat.FunctionalTests;

public sealed class ApplicationSmokeTests
{
    [Fact(Timeout = 10_000)]
    public Task Invalid_endpoint_is_reported_inline_by_the_wired_client_coordinator()
    {
        return RunInStaAsync(async () =>
        {
            using var loginForm = new LoginForm();
            await using var coordinator = new ClientCoordinator(loginForm);

            var connected = await coordinator.ConnectAsync("Ada", "endpoint-inválido");

            Assert.False(connected);
            Assert.True(coordinator.IsWired);
            Assert.Equal(
                "Usa una dirección y puerto, por ejemplo 127.0.0.1:55000.",
                loginForm.InlineErrorText);
        });
    }

    [Fact(Timeout = 10_000)]
    public Task Server_start_and_client_connect_are_wired_end_to_end()
    {
        return RunInStaAsync(async () =>
        {
            using var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            using var serverForm = new ServerMonitorForm();
            await using var serverCoordinator = new ServerCoordinator(serverForm);
            serverForm.SetSelectedPort(port);
            serverForm.Show();
            FindButton(serverForm, "Cambiar estado del servidor").PerformClick();
            await WaitUntilAsync(() => serverForm.CurrentState == ServerPresentationState.Listening);
            using var loginForm = new LoginForm();
            await using var clientCoordinator = new ClientCoordinator(loginForm);
            loginForm.SetConnectionInput("Ada", $"127.0.0.1:{serverCoordinator.ActualPort}");
            loginForm.Show();
            FindButton(loginForm, "Conectar al servidor").PerformClick();
            await WaitUntilAsync(() => clientCoordinator.ChatForm is not null);

            Assert.True(serverCoordinator.IsWired);
            Assert.Equal(ServerPresentationState.Listening, serverForm.CurrentState);
            Assert.True(clientCoordinator.IsWired);
            Assert.NotNull(clientCoordinator.ChatForm);
            Assert.True(clientCoordinator.Client?.IsConnected);
        });
    }

    [Fact(Timeout = 10_000)]
    public Task Unavailable_server_is_reported_inline()
    {
        return RunInStaAsync(async () =>
        {
            using var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var unavailablePort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            using var loginForm = new LoginForm();
            await using var coordinator = new ClientCoordinator(loginForm);

            var connected = await coordinator.ConnectAsync(
                "Ada",
                $"127.0.0.1:{unavailablePort}");

            Assert.False(connected);
            Assert.Equal(
                "No se pudo conectar al servidor. Verifica la dirección y el puerto.",
                loginForm.InlineErrorText);
        });
    }

    [Fact(Timeout = 10_000)]
    public Task Duplicate_name_is_presented_with_the_exact_inline_copy()
    {
        return RunInStaAsync(async () =>
        {
            using var serverForm = new ServerMonitorForm();
            await using var serverCoordinator = new ServerCoordinator(serverForm);
            await serverCoordinator.StartAsync(0);
            await using var firstClient = new ChatClient(
                Path.Combine(Path.GetTempPath(), $"chat-test-{Guid.NewGuid():N}"));
            await firstClient.ConnectAndRegisterAsync(
                "127.0.0.1",
                serverCoordinator.ActualPort,
                "Ada");
            using var loginForm = new LoginForm();
            await using var coordinator = new ClientCoordinator(loginForm);

            var connected = await coordinator.ConnectAsync(
                "aDa",
                $"127.0.0.1:{serverCoordinator.ActualPort}");

            Assert.False(connected);
            Assert.Equal("Ese nombre ya está en uso. Prueba con otro.", loginForm.InlineErrorText);
        });
    }

    [Fact(Timeout = 10_000)]
    public Task Chat_form_keeps_messages_and_transfers_isolated_by_peer()
    {
        return RunInStaAsync(
            () =>
            {
                using var form = new ChatForm("Me");
                form.SetConnectionState(true, "Conectado");
                form.SetUsers(
                [
                    new ChatUserView(2, "Grace", true),
                    new ChatUserView(3, "Linus", true)
                ]);
                Assert.True(form.SelectRecipient(2));
                form.AppendMessage(
                    2,
                    new ChatMessageView("g1", "Grace", "from-grace", DateTimeOffset.Now, false));
                form.AppendMessage(
                    3,
                    new ChatMessageView("l1", "Linus", "from-linus", DateTimeOffset.Now, false));
                form.AddOrUpdateFileTransfer(
                    2,
                    new FileTransferView("t1", "grace.bin", 40, "Recibiendo grace.bin... 40%", false));

                Assert.Equal(["from-grace"], form.VisibleMessages.Select(message => message.Text));
                Assert.Equal(["grace.bin"], form.VisibleTransfers.Select(transfer => transfer.FileName));
                Assert.Contains("Grace", form.ConversationHeaderText);

                Assert.True(form.SelectRecipient(3));
                Assert.Equal(["from-linus"], form.VisibleMessages.Select(message => message.Text));
                Assert.Empty(form.VisibleTransfers);
                Assert.Contains("Linus", form.ConversationHeaderText);

                form.AddOrUpdateFileTransfer(
                    2,
                    new FileTransferView("t1", "grace.bin", 100, "Recibido: grace.bin", false));
                Assert.Empty(form.VisibleTransfers);
                Assert.True(form.SelectRecipient(2));
                Assert.Equal(100, Assert.Single(form.VisibleTransfers).Progress);
                return Task.CompletedTask;
            });
    }

    [Fact(Timeout = 10_000)]
    public Task Delayed_send_and_transfer_progress_stay_bound_to_the_original_peer()
    {
        return RunInStaAsync(async () =>
        {
            var fakeClient = new FakeChatClient(
                [
                    new Chat.Protocol.ClientInfo(1, "Me"),
                    new Chat.Protocol.ClientInfo(2, "Grace"),
                    new Chat.Protocol.ClientInfo(3, "Linus")
                ]);
            var sendStarted = new TaskCompletionSource<byte>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSend = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            fakeClient.SendMessageHandler = async (targetId, _, _) =>
            {
                sendStarted.TrySetResult(targetId);
                await releaseSend.Task;
            };
            using var loginForm = new LoginForm();
            await using var coordinator = new ClientCoordinator(loginForm, _ => fakeClient);
            Assert.True(await coordinator.ConnectAsync("Me", "127.0.0.1:55000"));
            var form = Assert.IsType<ChatForm>(coordinator.ChatForm);
            Assert.True(form.SelectRecipient(2));
            form.SetMessageDraft("hello-grace");
            FindButton(form, "Enviar mensaje").PerformClick();
            Assert.Equal(2, await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.True(form.SelectRecipient(3));
            releaseSend.TrySetResult();
            await WaitUntilAsync(
                () =>
                {
                    form.SelectRecipient(2);
                    var found = form.VisibleMessages.Any(message => message.Text == "hello-grace");
                    form.SelectRecipient(3);
                    return found;
                });
            Assert.DoesNotContain(form.VisibleMessages, message => message.Text == "hello-grace");

            fakeClient.RaiseFileProgress(
                new FileProgressEventArgs(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    2,
                    "grace.bin",
                    65,
                    true));
            Assert.Empty(form.VisibleTransfers);
            Assert.True(form.SelectRecipient(2));
            Assert.Equal("grace.bin", Assert.Single(form.VisibleTransfers).FileName);
        });
    }

    [Fact(Timeout = 10_000)]
    public Task Unexpected_client_failures_are_rendered_inline_from_actual_button_events()
    {
        return RunInStaAsync(async () =>
        {
            var connectFailure = new FakeChatClient([])
            {
                ConnectException = new InvalidOperationException("connect exploded")
            };
            using var failedLogin = new LoginForm();
            await using (var failedCoordinator = new ClientCoordinator(failedLogin, _ => connectFailure))
            {
                failedLogin.SetConnectionInput("Ada", "127.0.0.1:55000");
                failedLogin.Show();
                FindButton(failedLogin, "Conectar al servidor").PerformClick();
                await WaitUntilAsync(() => failedLogin.InlineErrorText is not null);
                Assert.Contains("connect exploded", failedLogin.InlineErrorText);
            }

            var sendFailure = new FakeChatClient(
                [
                    new Chat.Protocol.ClientInfo(1, "Me"),
                    new Chat.Protocol.ClientInfo(2, "Grace")
                ])
            {
                SendMessageHandler = (_, _, _) =>
                    Task.FromException(new InvalidOperationException("send exploded"))
            };
            using var login = new LoginForm();
            await using var coordinator = new ClientCoordinator(login, _ => sendFailure);
            Assert.True(await coordinator.ConnectAsync("Me", "127.0.0.1:55000"));
            var form = Assert.IsType<ChatForm>(coordinator.ChatForm);
            Assert.True(form.SelectRecipient(2));
            form.SetMessageDraft("message");
            FindButton(form, "Enviar mensaje").PerformClick();
            await WaitUntilAsync(() => form.ComposerErrorText is not null);
            Assert.Contains("send exploded", form.ComposerErrorText);
        });
    }

    [Fact(Timeout = 10_000)]
    public Task Unexpected_server_start_and_stop_failures_become_status_and_log_entries()
    {
        return RunInStaAsync(async () =>
        {
            var startFailure = new FakeChatServer
            {
                StartException = new InvalidOperationException("start exploded")
            };
            using var failedStartForm = new ServerMonitorForm();
            await using (var failedStartCoordinator = new ServerCoordinator(
                failedStartForm,
                _ => startFailure))
            {
                failedStartForm.SetSelectedPort(55000);
                failedStartForm.Show();
                FindButton(failedStartForm, "Cambiar estado del servidor").PerformClick();
                await WaitUntilAsync(
                    () => failedStartForm.CurrentState == ServerPresentationState.Error);
                Assert.Contains("start exploded", failedStartForm.LogText);
            }

            var stopFailure = new FakeChatServer
            {
                StopException = new InvalidOperationException("stop exploded")
            };
            using var failedStopForm = new ServerMonitorForm();
            await using var failedStopCoordinator = new ServerCoordinator(
                failedStopForm,
                _ => stopFailure);
            failedStopForm.SetSelectedPort(55001);
            failedStopForm.Show();
            FindButton(failedStopForm, "Cambiar estado del servidor").PerformClick();
            await WaitUntilAsync(
                () => failedStopForm.CurrentState == ServerPresentationState.Listening);
            FindButton(failedStopForm, "Cambiar estado del servidor").PerformClick();
            await WaitUntilAsync(
                () => failedStopForm.CurrentState == ServerPresentationState.Error);
            Assert.Contains("stop exploded", failedStopForm.LogText);
        });
    }

    [Fact(Timeout = 10_000)]
    public Task Deferred_server_ui_action_failure_is_contained_and_logged()
    {
        return RunInStaAsync(async () =>
        {
            using var form = new ServerMonitorForm();
            form.Show();
            var unhandledCount = 0;
            ThreadExceptionEventHandler handler = (_, _) => unhandledCount++;
            System.Windows.Forms.Application.ThreadException += handler;
            try
            {
                await Task.Run(
                    () => form.DispatchUiAction(
                        () => throw new InvalidOperationException("deferred exploded")));
                await WaitUntilAsync(
                    () => form.CurrentState == ServerPresentationState.Error
                        && form.LogText.Contains(
                            "deferred exploded",
                            StringComparison.Ordinal));

                Assert.Equal(0, unhandledCount);
                Assert.Contains("deferred exploded", form.LogText);
            }
            finally
            {
                System.Windows.Forms.Application.ThreadException -= handler;
            }
        });
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
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
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
            if (child is Guna2Button button
                && string.Equals(button.AccessibleName, accessibleName, StringComparison.Ordinal))
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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeChatClient(IReadOnlyList<Chat.Protocol.ClientInfo> clients) : IChatClient
    {
        public Func<byte, string, CancellationToken, Task>? SendMessageHandler { get; set; }

        public Exception? ConnectException { get; init; }

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

        public event EventHandler<FileProgressEventArgs>? FileProgressChanged;

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

        public event EventHandler<GroupCreatedEventArgs>? GroupCreated
        {
            add { }
            remove { }
        }

        public event EventHandler<GroupMessageReceivedEventArgs>? GroupMessageReceived
        {
            add { }
            remove { }
        }

        public event EventHandler? Disconnected;

        public byte ClientId { get; private set; }

        public bool IsConnected { get; private set; }

        public IReadOnlyList<Chat.Protocol.ClientInfo> Clients { get; } = clients;

        public Task<Chat.Protocol.RegistrationResultPayload> ConnectAndRegisterAsync(
            string host,
            int port,
            string username,
            CancellationToken cancellationToken = default)
        {
            if (ConnectException is not null)
            {
                throw ConnectException;
            }

            ClientId = 1;
            IsConnected = true;
            return Task.FromResult(new Chat.Protocol.RegistrationResultPayload(true, 1, null));
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
            if (SendMessageHandler is not null)
            {
                return SendMessageHandler(targetId, text, cancellationToken);
            }

            return Task.CompletedTask;
        }

        public Task CreateGroupAsync(
            string groupName,
            IReadOnlyList<byte> memberIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendGroupMessageAsync(
            Guid groupId,
            string messageId,
            string text,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendEditMessageAsync(
            byte targetId,
            string messageId,
            string newText,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendDeleteMessageAsync(
            byte targetId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Guid> SendFileAsync(
            byte targetId,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Guid.NewGuid());
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void RaiseFileProgress(FileProgressEventArgs args)
        {
            FileProgressChanged?.Invoke(this, args);
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeChatServer : IChatServer
    {
        public Exception? StartException { get; init; }

        public Exception? StopException { get; init; }

        public event EventHandler<ServerClientsChangedEventArgs>? ClientsChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<ServerLogEventArgs>? LogEmitted
        {
            add { }
            remove { }
        }

        public event EventHandler<ServerRunningChangedEventArgs>? RunningChanged
        {
            add { }
            remove { }
        }

        public bool IsRunning { get; private set; }

        public int ActualPort { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (StartException is not null)
            {
                return Task.FromException(StartException);
            }

            IsRunning = true;
            ActualPort = 55001;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (StopException is not null)
            {
                return Task.FromException(StopException);
            }

            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }
}
