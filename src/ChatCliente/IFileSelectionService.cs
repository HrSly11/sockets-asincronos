namespace ChatCliente;

public interface IFileSelectionService
{
    IReadOnlyList<string> SelectFiles(IWin32Window owner);
}

public sealed class FileSelectionService : IFileSelectionService
{
    public IReadOnlyList<string> SelectFiles(IWin32Window owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecciona uno o más archivos",
            CheckFileExists = true,
            Multiselect = true
        };
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.FileNames
            : [];
    }
}
