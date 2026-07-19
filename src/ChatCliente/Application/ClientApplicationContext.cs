namespace ChatCliente.Application;

public sealed class ClientApplicationContext : ApplicationContext
{
    private readonly LoginForm loginForm;
    private readonly ClientCoordinator coordinator;

    public ClientApplicationContext()
    {
        loginForm = new LoginForm();
        coordinator = new ClientCoordinator(loginForm);
        loginForm.FormClosed += HandleLoginClosed;
        coordinator.ChatOpened += HandleChatOpened;
        loginForm.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            loginForm.FormClosed -= HandleLoginClosed;
            coordinator.ChatOpened -= HandleChatOpened;
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            loginForm.Dispose();
        }

        base.Dispose(disposing);
    }

    private void HandleLoginClosed(object? sender, FormClosedEventArgs args)
    {
        if (coordinator.ChatForm is null)
        {
            ExitThread();
        }
    }

    private void HandleChatOpened(object? sender, ChatForm chatForm)
    {
        chatForm.FormClosed += (_, _) => ExitThread();
    }
}
