using Chat.Presentation;
using Chat.Protocol;
using Guna.UI2.WinForms;

namespace ChatCliente;

public sealed partial class CreateGroupDialog : Form
{
    private readonly Guna2TextBox groupNameTextBox;
    private readonly CheckedListBox membersCheckedListBox;
    private readonly Guna2Button createButton;
    private readonly Guna2Button cancelButton;
    private readonly List<ClientInfo> availableClients;

    public string GroupName => groupNameTextBox.Text.Trim();

    public IReadOnlyList<byte> SelectedMemberIds
    {
        get
        {
            var selected = new List<byte>();
            for (var i = 0; i < membersCheckedListBox.Items.Count; i++)
            {
                if (membersCheckedListBox.GetItemChecked(i) && i < availableClients.Count)
                {
                    selected.Add(availableClients[i].Id);
                }
            }

            return selected;
        }
    }

    public CreateGroupDialog(IEnumerable<ClientInfo> connectedUsers)
    {
        availableClients = connectedUsers.ToList();
        Text = "Crear Nuevo Grupo";
        Width = 440;
        Height = 440;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Surface;
        Font = Theme.BodyFont();

        var nameLabel = new Label
        {
            Location = new Point(20, 16),
            Size = new Size(380, 22),
            Text = "Nombre del Grupo:",
            Font = Theme.BodyFont(9F, FontStyle.Bold),
            ForeColor = Theme.MainText
        };

        groupNameTextBox = new Guna2TextBox
        {
            Location = new Point(20, 42),
            Size = new Size(380, 40),
            PlaceholderText = "Ej. Equipo de Redes"
        };
        Theme.StyleTextBox(groupNameTextBox);

        var membersLabel = new Label
        {
            Location = new Point(20, 94),
            Size = new Size(380, 22),
            Text = "Selecciona los miembros:",
            Font = Theme.BodyFont(9F, FontStyle.Bold),
            ForeColor = Theme.MainText
        };

        membersCheckedListBox = new CheckedListBox
        {
            Location = new Point(20, 120),
            Size = new Size(380, 210),
            BackColor = Theme.MainBackground,
            ForeColor = Theme.MainText,
            BorderStyle = BorderStyle.FixedSingle,
            CheckOnClick = true
        };

        foreach (var user in availableClients)
        {
            membersCheckedListBox.Items.Add(user.Username, false);
        }

        createButton = new Guna2Button
        {
            Text = "Crear Grupo",
            Location = new Point(190, 345),
            Size = new Size(105, 38),
            DialogResult = DialogResult.OK
        };
        Theme.StylePrimaryButton(createButton);

        cancelButton = new Guna2Button
        {
            Text = "Cancelar",
            Location = new Point(305, 345),
            Size = new Size(95, 38),
            DialogResult = DialogResult.Cancel
        };

        createButton.Click += (sender, e) =>
        {
            if (string.IsNullOrWhiteSpace(GroupName))
            {
                MessageBox.Show(
                    "Por favor ingresa un nombre para el grupo.",
                    "Nombre requerido",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            if (SelectedMemberIds.Count == 0)
            {
                MessageBox.Show(
                    "Selecciona al menos un miembro para el grupo.",
                    "Miembros requeridos",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
        };

        Controls.AddRange([
            nameLabel,
            groupNameTextBox,
            membersLabel,
            membersCheckedListBox,
            createButton,
            cancelButton
        ]);
        AcceptButton = createButton;
    }
}
