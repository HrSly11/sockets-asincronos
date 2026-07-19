using Chat.Presentation;
using Guna.UI2.WinForms;

namespace ChatCliente;

public sealed partial class LoginForm : Form
{
    private readonly Guna2TextBox userNameTextBox;
    private readonly Guna2TextBox serverAddressTextBox;
    private readonly Guna2Button connectButton;
    private readonly Guna2Panel inlineMessagePanel;
    private readonly Label inlineMessageLabel;

    public LoginForm()
    {
        InitializeComponent();
        Text = "Chat Cliente";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 610);
        MinimumSize = new Size(440, 560);
        BackColor = Theme.MainBackground;
        Font = Theme.BodyFont();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        var card = new Guna2Panel
        {
            Size = new Size(402, 500),
            Location = new Point(49, 54),
            Anchor = AnchorStyles.None,
            BorderRadius = Theme.CardRadius,
            BorderThickness = 1,
            BorderColor = Theme.Border,
            FillColor = Theme.Surface,
            Padding = new Padding(38, 30, 38, 30)
        };
        card.ShadowDecoration.Enabled = true;
        card.ShadowDecoration.BorderRadius = Theme.CardRadius;
        card.ShadowDecoration.Depth = 12;
        card.ShadowDecoration.Color = Color.FromArgb(30, Theme.Sidebar);

        var iconSurface = new Guna2Panel
        {
            BorderRadius = Theme.IconSurfaceRadius,
            FillColor = Color.FromArgb(22, Theme.Primary),
            Size = new Size(56, 56),
            Location = new Point(173, 31)
        };
        var iconLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = FluentGlyphs.Chat,
            Font = Theme.IconFont(24F),
            ForeColor = Theme.Primary,
            TextAlign = ContentAlignment.MiddleCenter
        };
        iconSurface.Controls.Add(iconLabel);

        var titleLabel = new Label
        {
            AutoSize = false,
            Location = new Point(38, 103),
            Size = new Size(326, 34),
            Text = "Conéctate al chat",
            Font = Theme.TitleFont(),
            ForeColor = Theme.MainText,
            TextAlign = ContentAlignment.MiddleCenter
        };

        var subtitleLabel = new Label
        {
            AutoSize = false,
            Location = new Point(38, 139),
            Size = new Size(326, 38),
            Text = "Ingresa tus datos para comenzar.",
            Font = Theme.BodyFont(),
            ForeColor = Theme.SecondaryText,
            TextAlign = ContentAlignment.TopCenter
        };

        var userNameLabel = CreateFieldLabel("Nombre de usuario", 190);
        userNameTextBox = new Guna2TextBox
        {
            Location = new Point(38, 216),
            Size = new Size(326, 44),
            PlaceholderText = "Escribe tu nombre",
            MaxLength = 32
        };
        Theme.StyleTextBox(userNameTextBox);

        var addressLabel = CreateFieldLabel("Dirección del servidor", 277);
        serverAddressTextBox = new Guna2TextBox
        {
            Location = new Point(38, 303),
            Size = new Size(326, 44),
            Text = "127.0.0.1:55000",
            PlaceholderText = "127.0.0.1:55000",
            MaxLength = 80
        };
        Theme.StyleTextBox(serverAddressTextBox);

        inlineMessagePanel = new Guna2Panel
        {
            Location = new Point(38, 360),
            Size = new Size(326, 42),
            BorderRadius = Theme.InputRadius,
            FillColor = Color.FromArgb(20, Theme.Error),
            Visible = false,
            Padding = new Padding(12, 6, 12, 6)
        };
        inlineMessageLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Theme.Error,
            Font = Theme.MetadataFont(),
            TextAlign = ContentAlignment.MiddleLeft
        };
        inlineMessagePanel.Controls.Add(inlineMessageLabel);

        connectButton = new Guna2Button
        {
            Location = new Point(38, 416),
            Size = new Size(326, 46),
            Text = "Conectar",
            AccessibleName = "Conectar al servidor"
        };
        Theme.StylePrimaryButton(connectButton);
        connectButton.Click += HandleConnectClick;

        card.Controls.AddRange(
        [
            iconSurface,
            titleLabel,
            subtitleLabel,
            userNameLabel,
            userNameTextBox,
            addressLabel,
            serverAddressTextBox,
            inlineMessagePanel,
            connectButton
        ]);

        Controls.Add(card);
        AcceptButton = connectButton;
        ActiveControl = userNameTextBox;
    }

    public event EventHandler<LoginRequestedEventArgs>? ConnectRequested;

    public string UserName => userNameTextBox.Text.Trim();

    public string ServerAddress => serverAddressTextBox.Text.Trim();

    public string? InlineErrorText => string.IsNullOrEmpty(inlineMessageLabel.Text)
        ? null
        : inlineMessageLabel.Text;

    public void SetConnectionInput(string userName, string serverAddress)
    {
        userNameTextBox.Text = userName;
        serverAddressTextBox.Text = serverAddress;
    }

    public void SetBusy(bool isBusy)
    {
        connectButton.Enabled = !isBusy;
        userNameTextBox.Enabled = !isBusy;
        serverAddressTextBox.Enabled = !isBusy;
        connectButton.Text = isBusy ? "Conectando…" : "Conectar";
    }

    public void ShowInlineError(string message)
    {
        inlineMessageLabel.Text = message;
        inlineMessagePanel.Visible = true;
    }

    public void ShowUsernameAlreadyInUse()
    {
        ShowInlineError("Ese nombre ya está en uso. Prueba con otro.");
        userNameTextBox.Focus();
        userNameTextBox.SelectAll();
    }

    public void ClearInlineError()
    {
        inlineMessagePanel.Visible = false;
        inlineMessageLabel.Text = string.Empty;
    }

    private static Label CreateFieldLabel(string text, int top)
    {
        return new Label
        {
            AutoSize = false,
            Location = new Point(38, top),
            Size = new Size(326, 22),
            Text = text,
            Font = Theme.BodyFont(9F, FontStyle.Bold),
            ForeColor = Theme.MainText
        };
    }

    private void HandleConnectClick(object? sender, EventArgs e)
    {
        ClearInlineError();

        if (string.IsNullOrWhiteSpace(UserName))
        {
            ShowInlineError("Escribe tu nombre para continuar.");
            userNameTextBox.Focus();
            return;
        }

        if (!HasValidAddressShape(ServerAddress))
        {
            ShowInlineError("Usa una dirección y puerto, por ejemplo 127.0.0.1:55000.");
            serverAddressTextBox.Focus();
            return;
        }

        var connectRequested = ConnectRequested;
        if (connectRequested is null)
        {
            ShowInlineError("No se pudo procesar la conexión. Reinicia la aplicación.");
            return;
        }

        connectRequested.Invoke(this, new LoginRequestedEventArgs(UserName, ServerAddress));
    }

    private static bool HasValidAddressShape(string address)
    {
        var separator = address.LastIndexOf(':');
        return separator > 0
            && separator < address.Length - 1
            && int.TryParse(address[(separator + 1)..], out var port)
            && port is > 0 and <= 65535;
    }
}
