using Chat.Presentation;
using ChatCliente.Network;
using Guna.UI2.WinForms;

namespace ChatCliente;

public sealed partial class FileConflictDialog : Form
{
    public FileConflictDecision? Decision { get; private set; }

    // Required by the WinForms designer; runtime conflict resolution uses the
    // constructor below with the actual transfer context.
    public FileConflictDialog()
        : this(new FileConflictContext(
            "ejemplo.txt",
            Path.Combine(Path.GetTempPath(), "ejemplo.txt"),
            0,
            Guid.Empty))
    {
    }

    public FileConflictDialog(FileConflictContext conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        InitializeComponent();
        Text = "Archivo existente";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ClientSize = new Size(480, 210);
        BackColor = Theme.Surface;
        Font = Theme.BodyFont();

        var title = new Label
        {
            Location = new Point(28, 24),
            Size = new Size(424, 30),
            Text = "Ya existe un archivo con este nombre",
            Font = Theme.TitleFont(13F),
            ForeColor = Theme.MainText
        };
        var description = new Label
        {
            Location = new Point(28, 62),
            Size = new Size(424, 58),
            Text = $"“{conflict.FileName}” ya existe. ¿Deseas reemplazarlo o conservar ambos archivos?",
            Font = Theme.BodyFont(),
            ForeColor = Theme.SecondaryText
        };
        var keepBothButton = new Guna2Button
        {
            Location = new Point(196, 145),
            Size = new Size(124, 42),
            Text = "Conservar ambos",
            AccessibleName = "Conservar ambos"
        };
        Theme.StyleSecondaryButton(keepBothButton);
        keepBothButton.Click += (_, _) =>
        {
            Decision = FileConflictDecision.KeepBoth;
            DialogResult = DialogResult.OK;
            Close();
        };
        var replaceButton = new Guna2Button
        {
            Location = new Point(330, 145),
            Size = new Size(122, 42),
            Text = "Reemplazar",
            AccessibleName = "Reemplazar"
        };
        Theme.StylePrimaryButton(replaceButton);
        replaceButton.Click += (_, _) =>
        {
            Decision = FileConflictDecision.Replace;
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.AddRange([title, description, keepBothButton, replaceButton]);
        AcceptButton = replaceButton;
    }
}
