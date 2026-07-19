using ChatServidor.Application;

namespace ChatServidor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var form = new ServerMonitorForm();
        var coordinator = new ServerCoordinator(form);
        System.Windows.Forms.Application.Run(form);
        coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
