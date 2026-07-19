using ChatCliente.Application;

namespace ChatCliente;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new ClientApplicationContext());
    }
}
