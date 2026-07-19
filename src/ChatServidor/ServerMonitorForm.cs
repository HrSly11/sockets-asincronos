using Chat.Presentation;
using Guna.UI2.WinForms;

namespace ChatServidor;

public sealed partial class ServerMonitorForm : Form
{
    private readonly PulseStatusIndicator statusIndicator;
    private readonly Label statusLabel;
    private readonly Label statusDetailLabel;
    private readonly Guna2NumericUpDown portInput;
    private readonly Guna2Button stateButton;
    private readonly Guna2DataGridView clientsGrid;
    private readonly Label clientsEmptyLabel;
    private readonly RichTextBox logTextBox;
    private readonly Label logEmptyLabel;
    private ServerPresentationState currentState = ServerPresentationState.Stopped;
    private int reportingDispatchFailure;

    public ServerMonitorForm()
    {
        InitializeComponent();
        Text = "Monitor del Servidor";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(920, 650);
        BackColor = Theme.MainBackground;
        Font = Theme.BodyFont();
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.MainBackground,
            Padding = new Padding(28, 24, 28, 26),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 54F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 46F));

        var titlePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        var title = new Label
        {
            Location = new Point(0, 0),
            Size = new Size(520, 30),
            Text = "Monitor del Servidor",
            Font = Theme.TitleFont(16F),
            ForeColor = Theme.MainText
        };
        var subtitle = new Label
        {
            Location = new Point(1, 29),
            Size = new Size(620, 20),
            Text = "Supervisa el estado, los clientes y los eventos.",
            Font = Theme.MetadataFont(),
            ForeColor = Theme.SecondaryText
        };
        titlePanel.Controls.AddRange([title, subtitle]);

        var statusCard = new Guna2Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 14),
            BorderRadius = Theme.CardRadius,
            BorderThickness = 1,
            BorderColor = Theme.Border,
            FillColor = Theme.Surface,
            Padding = new Padding(22, 18, 22, 18)
        };
        statusCard.ShadowDecoration.Enabled = true;
        statusCard.ShadowDecoration.BorderRadius = Theme.CardRadius;
        statusCard.ShadowDecoration.Depth = 8;
        statusCard.ShadowDecoration.Color = Color.FromArgb(22, Theme.Sidebar);

        statusIndicator = new PulseStatusIndicator
        {
            Location = new Point(19, 26),
            Size = new Size(42, 42)
        };
        statusLabel = new Label
        {
            Location = new Point(68, 20),
            Size = new Size(480, 29),
            Text = "Servidor detenido",
            Font = Theme.TitleFont(14F),
            ForeColor = Theme.MainText,
            AutoEllipsis = true
        };
        statusDetailLabel = new Label
        {
            Location = new Point(69, 51),
            Size = new Size(500, 22),
            Text = "Configura el puerto e inicia el servicio.",
            Font = Theme.BodyFont(),
            ForeColor = Theme.SecondaryText,
            AutoEllipsis = true
        };

        var portLabel = new Label
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Size = new Size(60, 36),
            Text = "Puerto",
            Font = Theme.BodyFont(9F, FontStyle.Bold),
            ForeColor = Theme.MainText,
            TextAlign = ContentAlignment.MiddleLeft
        };
        portInput = new Guna2NumericUpDown
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Size = new Size(118, 42),
            Minimum = 1,
            Maximum = 65535,
            Value = 55000,
            BorderRadius = Theme.InputRadius,
            BorderColor = Theme.Border,
            FillColor = Theme.Surface,
            ForeColor = Theme.MainText,
            Font = Theme.BodyFont(),
            UpDownButtonFillColor = Theme.OtherMessage,
            UpDownButtonForeColor = Theme.MainText
        };
        portInput.FocusedState.BorderColor = Theme.Primary;

        stateButton = new Guna2Button
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Size = new Size(142, 42),
            Text = "Iniciar servidor",
            AccessibleName = "Cambiar estado del servidor"
        };
        Theme.StylePrimaryButton(stateButton);
        stateButton.Click += HandleStateButtonClick;

        void LayoutStatusActions(object? sender, EventArgs e)
        {
            stateButton.Location = new Point(statusCard.ClientSize.Width - stateButton.Width - 22, 26);
            portInput.Location = new Point(stateButton.Left - portInput.Width - 12, 26);
            var portTextWidth = TextRenderer.MeasureText(portLabel.Text, portLabel.Font).Width + 8;
            portLabel.Size = new Size(portTextWidth, 36);
            portLabel.Location = new Point(portInput.Left - portLabel.Width - 6, 29);

            var maxLeftWidth = Math.Max(100, portLabel.Left - statusLabel.Left - 12);
            statusLabel.Width = maxLeftWidth;
            statusDetailLabel.Width = maxLeftWidth;
        }

        statusCard.Resize += LayoutStatusActions;
        statusCard.Controls.AddRange(
            [statusIndicator, statusLabel, statusDetailLabel, portLabel, portInput, stateButton]);
        LayoutStatusActions(null, EventArgs.Empty);

        var clientsCard = CreateCardWithHeader(
            "Clientes conectados",
            "Sesiones visibles para la capa de aplicación.",
            out var clientsContent);
        clientsCard.Margin = new Padding(0, 0, 0, 14);

        clientsGrid = new Guna2DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.Surface,
            BorderStyle = BorderStyle.None,
            GridColor = Theme.Surface,
            CellBorderStyle = DataGridViewCellBorderStyle.None,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeight = 38,
            RowTemplate = { Height = 38 },
            EnableHeadersVisualStyles = false
        };
        StyleClientsGrid();
        clientsGrid.Columns.AddRange(
            CreateTextColumn("Id", "ID", 16F),
            CreateTextColumn("UserName", "Usuario", 25F),
            CreateTextColumn("IpAddress", "IP", 23F),
            CreateTextColumn("Port", "Puerto", 16F),
            CreateTextColumn("Status", "Estado", 20F));

        clientsEmptyLabel = new Label
        {
            AutoSize = false,
            Size = new Size(360, 54),
            Text = "Aún no hay clientes conectados",
            Font = Theme.BodyFont(),
            ForeColor = Theme.SecondaryText,
            BackColor = Theme.Surface,
            TextAlign = ContentAlignment.MiddleCenter
        };
        clientsGrid.Controls.Add(clientsEmptyLabel);
        clientsGrid.Resize += (_, _) => CenterOverlay(clientsGrid, clientsEmptyLabel);
        CenterOverlay(clientsGrid, clientsEmptyLabel);
        clientsContent.Controls.Add(clientsGrid);

        var logCard = CreateCardWithHeader(
            "Registro de eventos",
            "Actividad informada por la capa de aplicación.",
            out var logContent);
        logCard.Margin = Padding.Empty;

        var logSurface = new Guna2Panel
        {
            Dock = DockStyle.Fill,
            BorderRadius = Theme.CardRadius,
            FillColor = Theme.LogBackground,
            Padding = new Padding(14, 12, 14, 12)
        };
        logTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.LogBackground,
            ForeColor = Theme.LogText,
            BorderStyle = BorderStyle.None,
            Font = Theme.LogFont(),
            ReadOnly = true,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            TabStop = false
        };
        logEmptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "El registro de eventos está vacío",
            Font = Theme.LogFont(),
            ForeColor = Color.FromArgb(145, Theme.LogText),
            BackColor = Theme.LogBackground,
            TextAlign = ContentAlignment.MiddleCenter
        };
        logSurface.Controls.Add(logTextBox);
        logSurface.Controls.Add(logEmptyLabel);
        logContent.Controls.Add(logSurface);

        root.Controls.Add(titlePanel, 0, 0);
        root.Controls.Add(statusCard, 0, 1);
        root.Controls.Add(clientsCard, 0, 2);
        root.Controls.Add(logCard, 0, 3);
        Controls.Add(root);
    }

    public event EventHandler<ServerStateChangeRequestedEventArgs>? ServerStateChangeRequested;

    public int SelectedPort => Decimal.ToInt32(portInput.Value);

    public ServerPresentationState CurrentState => currentState;

    public string LogText => logTextBox.Text;

    public void SetSelectedPort(int port)
    {
        portInput.Value = Math.Clamp(
            port,
            Decimal.ToInt32(portInput.Minimum),
            Decimal.ToInt32(portInput.Maximum));
    }

    internal void DispatchUiAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RunOnUiThread(action);
    }

    public void SetServerState(
        ServerPresentationState state,
        int port,
        string? detail = null,
        bool pulse = true)
    {
        RunOnUiThread(() =>
        {
            currentState = state;
            var isListening = state == ServerPresentationState.Listening;
            statusIndicator.IsOnline = isListening;
            statusLabel.ForeColor = state == ServerPresentationState.Error ? Theme.Error : Theme.MainText;
            portInput.Value = Math.Clamp(port, Decimal.ToInt32(portInput.Minimum), Decimal.ToInt32(portInput.Maximum));

            statusLabel.Text = state switch
            {
                ServerPresentationState.Starting => $"Iniciando en puerto {port}",
                ServerPresentationState.Listening => $"Escuchando en puerto {port}",
                ServerPresentationState.Error => "No se pudo iniciar el servidor",
                _ => "Servidor detenido"
            };
            statusDetailLabel.Text = detail ?? state switch
            {
                ServerPresentationState.Starting => "Preparando el servicio…",
                ServerPresentationState.Listening => "Listo para recibir conexiones.",
                ServerPresentationState.Error => "Revisa el registro de eventos.",
                _ => "Configura el puerto e inicia el servicio."
            };

            stateButton.Text = isListening ? "Detener servidor" : "Iniciar servidor";
            portInput.Enabled = state is ServerPresentationState.Stopped or ServerPresentationState.Error;
            stateButton.Enabled = state != ServerPresentationState.Starting;
            if (isListening)
            {
                Theme.StyleSecondaryButton(stateButton);
            }
            else
            {
                Theme.StylePrimaryButton(stateButton);
            }

            if (pulse)
            {
                statusIndicator.PulseConnectionChange();
            }
        });
    }

    public void UpdateClients(IEnumerable<ConnectedClientView> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var snapshot = clients.ToArray();

        RunOnUiThread(() =>
        {
            clientsGrid.SuspendLayout();
            clientsGrid.Rows.Clear();
            foreach (var client in snapshot)
            {
                var rowIndex = clientsGrid.Rows.Add(
                    client.Id,
                    client.UserName,
                    client.IpAddress,
                    client.Port,
                    client.Status);
                clientsGrid.Rows[rowIndex].Cells[nameof(ConnectedClientView.Status)].Style.ForeColor =
                    string.Equals(client.Status, "Conectado", StringComparison.OrdinalIgnoreCase)
                        ? Theme.Success
                        : Theme.SecondaryText;
            }

            clientsEmptyLabel.Visible = snapshot.Length == 0;
            clientsGrid.ResumeLayout();
        });
    }

    public void AppendLog(string message, DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        RunOnUiThread(() =>
        {
            logEmptyLabel.Visible = false;
            var time = (timestamp ?? DateTimeOffset.Now).ToLocalTime();
            logTextBox.AppendText($"[{time:HH:mm:ss}] {message}{Environment.NewLine}");
            logTextBox.SelectionStart = logTextBox.TextLength;
            logTextBox.ScrollToCaret();
        });
    }

    public void ClearLog()
    {
        RunOnUiThread(() =>
        {
            logTextBox.Clear();
            logEmptyLabel.Visible = true;
        });
    }

    private static Guna2Panel CreateCardWithHeader(
        string title,
        string subtitle,
        out Panel content)
    {
        var card = new Guna2Panel
        {
            Dock = DockStyle.Fill,
            BorderRadius = Theme.CardRadius,
            BorderThickness = 1,
            BorderColor = Theme.Border,
            FillColor = Theme.Surface,
            Padding = new Padding(18, 14, 18, 16)
        };

        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 25,
            Text = title,
            Font = Theme.BodyFont(11F, FontStyle.Bold),
            ForeColor = Theme.MainText
        };
        var description = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = subtitle,
            Font = Theme.MetadataFont(),
            ForeColor = Theme.SecondaryText
        };
        content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 6, 0, 0)
        };

        card.Controls.Add(content);
        card.Controls.Add(description);
        card.Controls.Add(heading);
        return card;
    }

    private void StyleClientsGrid()
    {
        clientsGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.OtherMessage,
            ForeColor = Theme.MainText,
            Font = Theme.BodyFont(8F, FontStyle.Bold),
            SelectionBackColor = Theme.OtherMessage,
            SelectionForeColor = Theme.MainText,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0)
        };
        clientsGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.MainText,
            Font = Theme.BodyFont(),
            SelectionBackColor = Color.FromArgb(28, Theme.Primary),
            SelectionForeColor = Theme.MainText,
            Padding = new Padding(8, 0, 8, 0)
        };
        clientsGrid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.AlternatingRow,
            ForeColor = Theme.MainText,
            SelectionBackColor = Color.FromArgb(28, Theme.Primary),
            SelectionForeColor = Theme.MainText,
            Padding = new Padding(8, 0, 8, 0)
        };
    }

    private static DataGridViewTextBoxColumn CreateTextColumn(
        string name,
        string header,
        float fillWeight)
    {
        return new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            FillWeight = fillWeight,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
    }

    private static void CenterOverlay(Control parent, Control overlay)
    {
        overlay.Location = new Point(
            Math.Max(0, (parent.ClientSize.Width - overlay.Width) / 2),
            Math.Max(0, (parent.ClientSize.Height - overlay.Height) / 2));
        overlay.BringToFront();
    }

    private void HandleStateButtonClick(object? sender, EventArgs e)
    {
        var shouldStart = currentState != ServerPresentationState.Listening;
        var stateChangeRequested = ServerStateChangeRequested;
        if (stateChangeRequested is null)
        {
            var action = shouldStart ? "iniciar" : "detener";
            var detail = $"No se pudo {action}. Reinicia la aplicación.";
            SetServerState(
                ServerPresentationState.Error,
                SelectedPort,
                detail,
                pulse: true);
            AppendLog($"No se pudo {action} el servidor. Reinicia la aplicación.");
            return;
        }

        stateChangeRequested.Invoke(
            this,
            new ServerStateChangeRequestedEventArgs(shouldStart, SelectedPort));
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => ExecuteUiAction(action));
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ObjectDisposedException)
            {
            }

            return;
        }

        ExecuteUiAction(action);
    }

    private void ExecuteUiAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ReportDispatchFailure(exception);
        }
    }

    private void ReportDispatchFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref reportingDispatchFailure, 1) != 0)
        {
            return;
        }

        try
        {
            currentState = ServerPresentationState.Error;
            statusIndicator.IsOnline = false;
            statusLabel.ForeColor = Theme.Error;
            statusLabel.Text = "Error al actualizar el monitor";
            statusDetailLabel.Text = exception.Message;
            stateButton.Text = "Iniciar servidor";
            stateButton.Enabled = true;
            portInput.Enabled = true;
            Theme.StylePrimaryButton(stateButton);
            logEmptyLabel.Visible = false;
            logTextBox.AppendText(
                $"[{DateTimeOffset.Now:HH:mm:ss}] Error de interfaz: {exception.Message}"
                + Environment.NewLine);
            logTextBox.SelectionStart = logTextBox.TextLength;
            logTextBox.ScrollToCaret();
        }
        catch (Exception)
        {
        }
        finally
        {
            Volatile.Write(ref reportingDispatchFailure, 0);
        }
    }
}
