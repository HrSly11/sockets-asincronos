using Chat.Presentation;
using Guna.UI2.WinForms;

namespace ChatCliente;

public sealed partial class ChatForm : Form
{
    private readonly IFileSelectionService fileSelectionService;
    private readonly DoubleBufferedFlowLayoutPanel usersFlow;
    private readonly EmptyStateControl usersEmptyState;
    private readonly DoubleBufferedFlowLayoutPanel messagesFlow;
    private readonly EmptyStateControl messagesEmptyState;
    private readonly EmptyStateControl activityEmptyState;
    private readonly PulseStatusIndicator connectionIndicator;
    private readonly Label connectionStatusLabel;
    private readonly Label currentUserLabel;
    private Label conversationTitleLabel = null!;
    private Label conversationSubtitleLabel = null!;
    private Guna2TextBox messageTextBox = null!;
    private Guna2Button sendButton = null!;
    private Guna2Button attachButton = null!;
    private Guna2Button micButton = null!;
    private Guna2Panel composerMessagePanel = null!;
    private Label composerMessageLabel = null!;
    private Guna2Panel? activeCallPanel;
    private Label? activeCallLabel;
    private Guna2Button? endCallButton;
    private Guid? currentActiveCallId;
    private readonly Media.WaveAudioRecorder audioRecorder = new();
    private DateTime recordStartTime;
    private readonly Dictionary<string, FileTransferCard> transferCards = [];
    private readonly Dictionary<byte, Guna2Panel> userRows = [];
    private readonly Dictionary<byte, string> userNames = [];
    private readonly Dictionary<byte, ConversationState> conversations = [];
    private readonly Dictionary<Guid, Guna2Panel> groupRows = [];
    private readonly Dictionary<Guid, string> groupNames = [];
    private readonly Dictionary<Guid, ConversationState> groupConversations = [];
    private byte? selectedRecipientId;
    private string? selectedRecipientName;
    private Guid? selectedGroupId;
    private string? selectedGroupName;
    private bool isConnected;

    public ChatForm(
        string currentUserName = "Usuario",
        IFileSelectionService? fileSelectionService = null)
    {
        this.fileSelectionService = fileSelectionService ?? new FileSelectionService();
        InitializeComponent();
        Text = "Chat Cliente";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 760);
        MinimumSize = new Size(1000, 640);
        BackColor = Theme.MainBackground;
        Font = Theme.BodyFont();
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.MainBackground,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245F));
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var sidebar = new Guna2Panel
        {
            Dock = DockStyle.Fill,
            FillColor = Theme.Sidebar,
            BackColor = Theme.Sidebar,
            Padding = new Padding(18, 22, 18, 18),
            Margin = Padding.Empty
        };

        var brandRow = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            BackColor = Theme.Sidebar
        };
        var brandIcon = new Label
        {
            Location = new Point(0, 2),
            Size = new Size(38, 38),
            Text = FluentGlyphs.Chat,
            Font = Theme.IconFont(19F),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter
        };
        var brandTitle = new Label
        {
            Location = new Point(44, 2),
            Size = new Size(165, 24),
            Text = "Chat de Redes",
            Font = Theme.BodyFont(11F, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var brandSubtitle = new Label
        {
            Location = new Point(44, 25),
            Size = new Size(165, 18),
            Text = "Espacio compartido",
            Font = Theme.MetadataFont(),
            ForeColor = Color.FromArgb(175, 255, 255, 255)
        };
        brandRow.Controls.AddRange([brandIcon, brandTitle, brandSubtitle]);

        var createGroupButton = new Guna2Button
        {
            Text = "+ Nuevo Grupo",
            Dock = DockStyle.Top,
            Height = 36,
            Margin = new Padding(12, 8, 12, 8),
            BorderRadius = Theme.CardRadius
        };
        Theme.StylePrimaryButton(createGroupButton);
        createGroupButton.Click += (sender, e) => CreateGroupRequested?.Invoke(this, EventArgs.Empty);

        var usersHeading = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(2, 16, 0, 0),
            Text = "USUARIOS Y GRUPOS",
            Font = Theme.BodyFont(8F, FontStyle.Bold),
            ForeColor = Color.FromArgb(170, 255, 255, 255),
            BackColor = Theme.Sidebar
        };

        usersFlow = new DoubleBufferedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Sidebar,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 4, 0, 4)
        };
        usersEmptyState = new EmptyStateControl
        {
            Width = 200,
            Message = "Aún no hay usuarios conectados",
            Glyph = FluentGlyphs.People
        };
        SetEmptyStateColors(usersEmptyState, Color.FromArgb(160, 255, 255, 255));
        usersFlow.Controls.Add(usersEmptyState);

        var connectionCard = new Guna2Panel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            BorderRadius = Theme.CardRadius,
            FillColor = Theme.SidebarSurface,
            BackColor = Theme.Sidebar,
            Padding = new Padding(10)
        };
        connectionIndicator = new PulseStatusIndicator
        {
            Location = new Point(8, 18),
            Size = new Size(24, 24)
        };
        connectionStatusLabel = new Label
        {
            Location = new Point(38, 9),
            Size = new Size(165, 22),
            Text = "Sin conexión",
            Font = Theme.BodyFont(9F, FontStyle.Bold),
            ForeColor = Color.White
        };
        currentUserLabel = new Label
        {
            Location = new Point(38, 32),
            Size = new Size(165, 18),
            Text = currentUserName,
            Font = Theme.MetadataFont(),
            ForeColor = Color.FromArgb(175, 255, 255, 255)
        };
        connectionCard.Controls.AddRange([connectionIndicator, connectionStatusLabel, currentUserLabel]);

        sidebar.Controls.Add(usersFlow);
        sidebar.Controls.Add(usersHeading);
        sidebar.Controls.Add(createGroupButton);
        sidebar.Controls.Add(brandRow);
        sidebar.Controls.Add(connectionCard);

        var chatColumn = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.MainBackground,
            RowCount = 3,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        chatColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));
        chatColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        chatColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 104F));

        var chatHeader = CreateChatHeader();
        messagesFlow = new DoubleBufferedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.MainBackground,
            Padding = new Padding(24, 18, 24, 18),
            Margin = Padding.Empty
        };
        messagesFlow.Resize += (_, _) => RealignMessageRows();

        messagesEmptyState = new EmptyStateControl
        {
            Message = "Aún no hay mensajes. Inicia la conversación.",
            Glyph = FluentGlyphs.Chat,
            Width = 520,
            Height = 150,
            Margin = new Padding(0, 92, 0, 0)
        };
        messagesFlow.Controls.Add(messagesEmptyState);

        var composer = CreateComposer();
        chatColumn.Controls.Add(chatHeader, 0, 0);
        chatColumn.Controls.Add(messagesFlow, 0, 1);
        chatColumn.Controls.Add(composer, 0, 2);

        activityEmptyState = new EmptyStateControl
        {
            Dock = DockStyle.Top,
            Height = 130,
            Message = "Aún no hay transferencias",
            Glyph = FluentGlyphs.Attach
        };
        var activityColumn = CreateActivityColumn(activityEmptyState);

        rootLayout.Controls.Add(sidebar, 0, 0);
        rootLayout.Controls.Add(chatColumn, 1, 0);
        rootLayout.Controls.Add(activityColumn, 2, 0);
        Controls.Add(rootLayout);
        AcceptButton = sendButton;
        SetComposerEnabled(false);
    }

    public event EventHandler<MessageRequestedEventArgs>? SendMessageRequested;

    public event EventHandler<EditMessageRequestedEventArgs>? EditMessageRequested;

    public event EventHandler<DeleteMessageRequestedEventArgs>? DeleteMessageRequested;

    public event EventHandler<AttachmentRequestedEventArgs>? AttachmentRequested;

    public event EventHandler<SendGroupMessageRequestedEventArgs>? SendGroupMessageRequested;

    public event EventHandler<SendVoiceNoteRequestedEventArgs>? SendVoiceNoteRequested;

    public event EventHandler<CallRequestedEventArgs>? StartCallRequested;

    public event EventHandler<CallAnsweredRequestedEventArgs>? AnswerCallRequested;

    public event EventHandler<EndCallRequestedEventArgs>? EndCallRequested;

    public event EventHandler? CreateGroupRequested;

    public byte? SelectedRecipientId => selectedRecipientId;

    public Guid? SelectedGroupId => selectedGroupId;

    public IReadOnlyList<ChatMessageView> VisibleMessages => GetSelectedEntries()
        .OfType<MessageConversationEntry>()
        .Select(entry => entry.Message)
        .ToArray();

    public IReadOnlyList<FileTransferView> VisibleTransfers => GetSelectedEntries()
        .OfType<FileConversationEntry>()
        .Select(entry => entry.Transfer)
        .ToArray();

    public HashSet<string> GetExistingMessageIds(byte peerId)
    {
        return GetConversation(peerId).Entries
            .OfType<MessageConversationEntry>()
            .Select(entry => entry.Message.Id)
            .ToHashSet();
    }

    public string ConversationHeaderText => conversationTitleLabel.Text;

    public string? ComposerErrorText => string.IsNullOrEmpty(composerMessageLabel.Text)
        ? null
        : composerMessageLabel.Text;

    public bool IsComposerEnabled =>
        messageTextBox.Enabled
        && sendButton.Enabled
        && attachButton.Enabled;

    public void SetCurrentUser(string displayName)
    {
        currentUserLabel.Text = displayName;
    }

    public void SetConnectionState(bool isConnected, string statusText, bool pulse = true)
    {
        this.isConnected = isConnected;
        connectionIndicator.IsOnline = isConnected;
        connectionStatusLabel.Text = statusText;
        SetComposerEnabled(isConnected && selectedRecipientId.HasValue);
        if (pulse)
        {
            connectionIndicator.PulseConnectionChange();
        }
    }

    public void SetUsers(IEnumerable<ChatUserView> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        usersFlow.SuspendLayout();
        var oldUserRows = usersFlow.Controls
            .Cast<Control>()
            .Where(control => !ReferenceEquals(control, usersEmptyState))
            .ToArray();
        usersFlow.Controls.Clear();
        foreach (var oldRow in oldUserRows)
        {
            oldRow.Dispose();
        }
        userRows.Clear();
        userNames.Clear();

        var userList = users
            .DistinctBy(user => user.Id)
            .DistinctBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var user in userList)
        {
            userNames[user.Id] = user.DisplayName;
        }

        if (selectedRecipientId.HasValue
            && userList.All(user => user.Id != selectedRecipientId.Value))
        {
            selectedRecipientId = null;
            selectedRecipientName = null;
            UpdateConversationHeader();
            RenderSelectedConversation();
        }
        else if (selectedRecipientId.HasValue)
        {
            selectedRecipientName = userNames[selectedRecipientId.Value];
            UpdateConversationHeader();
        }

        usersEmptyState.Visible = userList.Length == 0;
        if (userList.Length == 0)
        {
            usersFlow.Controls.Add(usersEmptyState);
        }
        else
        {
            foreach (var user in userList)
            {
                var row = CreateUserRow(user);
                userRows[user.Id] = row;
                usersFlow.Controls.Add(row);
            }
        }

        usersFlow.ResumeLayout();
        SetComposerEnabled(isConnected && selectedRecipientId.HasValue);
    }

    public void AppendMessage(byte peerId, ChatMessageView message)
    {
        ArgumentNullException.ThrowIfNull(message);
        GetConversation(peerId).Entries.Add(new MessageConversationEntry(message));
        if (selectedRecipientId == peerId)
        {
            RenderSelectedConversation();
        }
    }

    public void UpdateEditedMessage(byte peerId, string messageId, string newText)
    {
        var conversation = GetConversation(peerId);
        var entry = conversation.Entries
            .OfType<MessageConversationEntry>()
            .FirstOrDefault(e => e.Message.Id == messageId);
        if (entry is not null)
        {
            entry.Message = entry.Message with { Text = newText, IsEdited = true };
            if (selectedRecipientId == peerId)
            {
                RenderSelectedConversation();
            }
        }
    }

    public void MarkMessageDeleted(byte peerId, string messageId)
    {
        var conversation = GetConversation(peerId);
        var entry = conversation.Entries
            .OfType<MessageConversationEntry>()
            .FirstOrDefault(e => e.Message.Id == messageId);
        if (entry is not null)
        {
            entry.Message = entry.Message with { IsDeleted = true };
            if (selectedRecipientId == peerId)
            {
                RenderSelectedConversation();
            }
        }
    }

    public void AddOrUpdateFileTransfer(byte peerId, FileTransferView transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var conversation = GetConversation(peerId);
        var existing = conversation.Entries
            .OfType<FileConversationEntry>()
            .FirstOrDefault(entry => string.Equals(
                entry.Transfer.Id,
                transfer.Id,
                StringComparison.Ordinal));
        if (existing is null)
        {
            conversation.Entries.Add(new FileConversationEntry(transfer));
        }
        else
        {
            existing.Transfer = transfer;
        }

        if (selectedRecipientId == peerId)
        {
            if (existing is not null && transferCards.TryGetValue(transfer.Id, out var card))
            {
                card.UpdateTransfer(transfer.FileName, transfer.Progress, transfer.StatusText);
            }
            else
            {
                if (messagesFlow.Controls.Contains(messagesEmptyState))
                {
                    messagesFlow.Controls.Remove(messagesEmptyState);
                }

                var scrollMargin = messagesFlow.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
                var rowWidth = Math.Max(280, messagesFlow.ClientSize.Width - scrollMargin - 48);
                var maxCardWidth = Math.Min(380, Math.Max(240, rowWidth - 60));

                var newCard = new FileTransferCard
                {
                    TransferId = transfer.Id,
                    Width = maxCardWidth
                };
                newCard.UpdateTransfer(transfer.FileName, transfer.Progress, transfer.StatusText);
                transferCards[transfer.Id] = newCard;

                var row = CreateAlignedRow(newCard, transfer.IsOwn, newCard.Height + 22);
                messagesFlow.Controls.Add(row);
                RealignMessageRows();
                messagesFlow.ScrollControlIntoView(row);
            }

            activityEmptyState.Message = transfer.Progress >= 100
                ? "Transferencia completada"
                : "Transferencia en curso";
            activityEmptyState.Glyph = FluentGlyphs.ForFile(transfer.FileName);
        }
    }

    public void SetComposerEnabled(bool isEnabled)
    {
        messageTextBox.Enabled = isEnabled;
        sendButton.Enabled = isEnabled;
        attachButton.Enabled = isEnabled;
    }

    public void ShowComposerError(string message)
    {
        composerMessageLabel.Text = message;
        composerMessagePanel.Visible = true;
    }

    public void ClearComposerError()
    {
        composerMessagePanel.Visible = false;
        composerMessageLabel.Text = string.Empty;
    }

    public void ClearMessageDraft()
    {
        messageTextBox.Clear();
    }

    public void SetMessageDraft(string message)
    {
        messageTextBox.Text = message;
    }

    public bool SelectRecipient(byte userId)
    {
        if (!userNames.TryGetValue(userId, out var displayName))
        {
            return false;
        }

        selectedRecipientId = userId;
        selectedRecipientName = displayName;
        selectedGroupId = null;
        selectedGroupName = null;

        foreach (var (id, row) in userRows)
        {
            row.FillColor = id == userId
                ? Color.FromArgb(70, Theme.Primary)
                : Color.Transparent;
        }

        foreach (var (_, row) in groupRows)
        {
            row.FillColor = Color.Transparent;
        }

        UpdateConversationHeader();
        RenderSelectedConversation();
        SetComposerEnabled(isConnected);
        ClearComposerError();
        messageTextBox.Focus();
        return true;
    }

    private Guna2Panel CreateChatHeader()
    {
        var header = new Guna2Panel
        {
            Dock = DockStyle.Fill,
            FillColor = Theme.Surface,
            BackColor = Theme.Surface,
            BorderThickness = 0,
            Padding = new Padding(24, 14, 24, 12),
            Margin = Padding.Empty
        };

        conversationTitleLabel = new Label
        {
            Location = new Point(24, 14),
            Size = new Size(340, 27),
            Text = "Selecciona un usuario",
            Font = Theme.TitleFont(14F),
            ForeColor = Theme.MainText
        };
        conversationSubtitleLabel = new Label
        {
            Location = new Point(25, 42),
            Size = new Size(420, 20),
            Text = "Elige a quién enviar mensajes y archivos.",
            Font = Theme.MetadataFont(),
            ForeColor = Theme.SecondaryText
        };

        var callButton = new Guna2Button
        {
            Location = new Point(480, 16),
            Size = new Size(110, 40),
            Text = "📞 Llamar",
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        Theme.StyleSecondaryButton(callButton);
        callButton.Click += (s, e) =>
        {
            if (selectedRecipientId.HasValue)
            {
                StartCallRequested?.Invoke(this, new CallRequestedEventArgs(selectedRecipientId.Value));
            }
        };

        var divider = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 1,
            BackColor = Theme.Border
        };
        header.Controls.AddRange([conversationTitleLabel, conversationSubtitleLabel, callButton, divider]);
        return header;
    }

    private Guna2Panel CreateComposer()
    {
        var composer = new Guna2Panel
        {
            Dock = DockStyle.Fill,
            FillColor = Theme.Surface,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 16, 20, 16),
            Margin = Padding.Empty
        };

        attachButton = new Guna2Button
        {
            Location = new Point(20, 20),
            Size = new Size(46, 46),
            Text = FluentGlyphs.Attach,
            AccessibleName = "Adjuntar archivo"
        };
        Theme.StyleSecondaryButton(attachButton);
        attachButton.Font = Theme.IconFont(16F);
        attachButton.Click += HandleAttachmentClick;

        micButton = new Guna2Button
        {
            Location = new Point(72, 20),
            Size = new Size(46, 46),
            Text = "🎙️",
            AccessibleName = "Grabar nota de voz"
        };
        Theme.StyleSecondaryButton(micButton);
        micButton.Click += HandleMicClick;

        messageTextBox = new Guna2TextBox
        {
            Location = new Point(126, 20),
            Height = 46,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            PlaceholderText = "Escribe un mensaje",
            MaxLength = 2000
        };
        Theme.StyleTextBox(messageTextBox);
        messageTextBox.KeyDown += HandleMessageKeyDown;

        sendButton = new Guna2Button
        {
            Size = new Size(112, 46),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Text = "Enviar",
            ImageAlign = HorizontalAlignment.Right,
            AccessibleName = "Enviar mensaje"
        };
        Theme.StylePrimaryButton(sendButton);
        sendButton.Click += HandleSendClick;

        composerMessagePanel = new Guna2Panel
        {
            Location = new Point(76, 70),
            Height = 24,
            BorderRadius = Theme.InputRadius,
            FillColor = Color.FromArgb(20, Theme.Error),
            Visible = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
        };
        composerMessageLabel = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 2, 8, 0),
            Font = Theme.MetadataFont(),
            ForeColor = Theme.Error,
            TextAlign = ContentAlignment.MiddleLeft
        };
        composerMessagePanel.Controls.Add(composerMessageLabel);

        void LayoutComposer(object? sender, EventArgs e)
        {
            sendButton.Location = new Point(composer.ClientSize.Width - 132, 20);
            messageTextBox.Width = Math.Max(160, sendButton.Left - messageTextBox.Left - 10);
            composerMessagePanel.Width = messageTextBox.Width;
        }

        composer.Resize += LayoutComposer;
        composer.Controls.AddRange([attachButton, micButton, messageTextBox, sendButton, composerMessagePanel]);
        LayoutComposer(null, EventArgs.Empty);
        return composer;
    }

    private Guna2Panel CreateActivityColumn(EmptyStateControl emptyState)
    {
        var panel = new Guna2Panel
        {
            Dock = DockStyle.Fill,
            FillColor = Theme.Surface,
            BackColor = Theme.Surface,
            BorderThickness = 1,
            BorderColor = Theme.Border,
            Padding = new Padding(18, 18, 18, 18),
            Margin = Padding.Empty
        };

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = "Actividad",
            Font = Theme.TitleFont(12F),
            ForeColor = Theme.MainText
        };
        var description = new Label
        {
            Dock = DockStyle.Top,
            Height = 62,
            Text = "Las transferencias aparecen como tarjetas dentro del hilo.",
            Font = Theme.BodyFont(),
            ForeColor = Theme.SecondaryText
        };
        var divider = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Theme.Border,
            Margin = new Padding(0, 0, 0, 16)
        };

        panel.Controls.Add(emptyState);
        panel.Controls.Add(divider);
        panel.Controls.Add(description);
        panel.Controls.Add(title);
        return panel;
    }

    private Guna2Panel CreateUserRow(ChatUserView user)
    {
        var row = new Guna2Panel
        {
            Size = new Size(200, 42),
            BorderRadius = Theme.InputRadius,
            FillColor = selectedRecipientId == user.Id
                ? Color.FromArgb(70, Theme.Primary)
                : Color.Transparent,
            Cursor = Cursors.Hand,
            Tag = user.Id,
            Margin = new Padding(0, 2, 0, 2)
        };
        var indicator = new PulseStatusIndicator
        {
            Location = new Point(3, 9),
            Size = new Size(24, 24),
            IsOnline = user.IsOnline
        };
        var name = new Label
        {
            Location = new Point(32, 4),
            Size = new Size(160, 34),
            Text = user.DisplayName,
            AutoEllipsis = true,
            Font = Theme.BodyFont(),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft
        };
        row.Controls.AddRange([indicator, name]);
        void SelectUser(object? sender, EventArgs e) => SelectRecipient(user.Id);
        row.Click += SelectUser;
        indicator.Click += SelectUser;
        name.Click += SelectUser;
        return row;
    }

    private ConversationState GetConversation(byte peerId)
    {
        if (!conversations.TryGetValue(peerId, out var conversation))
        {
            conversation = new ConversationState();
            conversations.Add(peerId, conversation);
        }

        return conversation;
    }

    private IReadOnlyList<ConversationEntry> GetSelectedEntries()
    {
        if (selectedGroupId.HasValue && groupConversations.TryGetValue(selectedGroupId.Value, out var groupConv))
        {
            return groupConv.Entries;
        }

        if (selectedRecipientId.HasValue && conversations.TryGetValue(selectedRecipientId.Value, out var userConv))
        {
            return userConv.Entries;
        }

        return [];
    }

    public void AddGroup(ChatGroupView group)
    {
        groupNames[group.Id] = group.GroupName;
        if (!groupConversations.ContainsKey(group.Id))
        {
            groupConversations[group.Id] = new ConversationState();
        }

        var row = CreateGroupRow(group);
        groupRows[group.Id] = row;
        usersFlow.Controls.Add(row);
        SelectGroup(group.Id);
    }

    public bool SelectGroup(Guid groupId)
    {
        if (!groupNames.TryGetValue(groupId, out var groupName))
        {
            return false;
        }

        selectedRecipientId = null;
        selectedRecipientName = null;
        selectedGroupId = groupId;
        selectedGroupName = groupName;

        foreach (var (_, row) in userRows)
        {
            row.FillColor = Color.Transparent;
        }

        foreach (var (id, row) in groupRows)
        {
            row.FillColor = id == groupId
                ? Color.FromArgb(70, Theme.Primary)
                : Color.Transparent;
        }

        UpdateConversationHeader();
        RenderSelectedConversation();
        SetComposerEnabled(isConnected);
        ClearComposerError();
        messageTextBox.Focus();
        return true;
    }

    public void AppendGroupMessage(Guid groupId, ChatMessageView message)
    {
        if (!groupConversations.TryGetValue(groupId, out var conversation))
        {
            conversation = new ConversationState();
            groupConversations[groupId] = conversation;
        }

        conversation.Entries.Add(new MessageConversationEntry(message));
        if (selectedGroupId == groupId)
        {
            messagesEmptyState.Visible = false;
            var row = CreateMessageRow(message);
            messagesFlow.Controls.Add(row);
            if (messagesFlow.Controls.Count > 0)
            {
                messagesFlow.ScrollControlIntoView(messagesFlow.Controls[messagesFlow.Controls.Count - 1]);
            }
        }
    }

    private Guna2Panel CreateGroupRow(ChatGroupView group)
    {
        var row = new Guna2Panel
        {
            Size = new Size(200, 42),
            BorderRadius = Theme.InputRadius,
            FillColor = selectedGroupId == group.Id
                ? Color.FromArgb(70, Theme.Primary)
                : Color.Transparent,
            Cursor = Cursors.Hand,
            Tag = group.Id,
            Margin = new Padding(0, 2, 0, 2)
        };
        var icon = new Label
        {
            Location = new Point(3, 4),
            Size = new Size(26, 34),
            Text = "👥",
            Font = Theme.BodyFont(12F),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var name = new Label
        {
            Location = new Point(32, 4),
            Size = new Size(160, 34),
            Text = group.GroupName,
            AutoEllipsis = true,
            Font = Theme.BodyFont(9F, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft
        };
        row.Controls.AddRange([icon, name]);
        void OnSelectGroup(object? sender, EventArgs e) => SelectGroup(group.Id);
        row.Click += OnSelectGroup;
        icon.Click += OnSelectGroup;
        name.Click += OnSelectGroup;
        return row;
    }

    private void UpdateConversationHeader()
    {
        if (selectedGroupId.HasValue && selectedGroupName is not null)
        {
            conversationTitleLabel.Text = $"👥 {selectedGroupName}";
            conversationSubtitleLabel.Text = "Chat de grupo";
        }
        else if (selectedRecipientId.HasValue && selectedRecipientName is not null)
        {
            conversationTitleLabel.Text = selectedRecipientName;
            conversationSubtitleLabel.Text = "Chat individual por socket TCP";
        }
        else
        {
            conversationTitleLabel.Text = "Selecciona un usuario o grupo";
            conversationSubtitleLabel.Text = "Elige a quién enviar mensajes y archivos.";
        }
    }

    private void RenderSelectedConversation()
    {
        messagesFlow.SuspendLayout();
        var oldRows = messagesFlow.Controls
            .Cast<Control>()
            .Where(control => !ReferenceEquals(control, messagesEmptyState))
            .ToArray();
        messagesFlow.Controls.Clear();
        foreach (var oldRow in oldRows)
        {
            oldRow.Dispose();
        }

        transferCards.Clear();
        var entries = GetSelectedEntries();
        if (entries.Count == 0)
        {
            messagesFlow.Controls.Add(messagesEmptyState);
            activityEmptyState.Message = "Aún no hay transferencias";
            activityEmptyState.Glyph = FluentGlyphs.Attach;
            messagesFlow.ResumeLayout();
            return;
        }

        FileTransferView? latestTransfer = null;
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case MessageConversationEntry messageEntry:
                    messagesFlow.Controls.Add(CreateMessageRow(messageEntry.Message));
                    break;
                case FileConversationEntry fileEntry:
                    latestTransfer = fileEntry.Transfer;
                    var card = new FileTransferCard
                    {
                        TransferId = fileEntry.Transfer.Id,
                        Width = Math.Min(390, Math.Max(280, messagesFlow.ClientSize.Width - 130))
                    };
                    card.UpdateTransfer(
                        fileEntry.Transfer.FileName,
                        fileEntry.Transfer.Progress,
                        fileEntry.Transfer.StatusText);
                    transferCards[fileEntry.Transfer.Id] = card;
                    messagesFlow.Controls.Add(
                        CreateAlignedRow(
                            card,
                            fileEntry.Transfer.IsOwn,
                            card.Height + 22));
                    break;
                case VoiceConversationEntry voiceEntry:
                    messagesFlow.Controls.Add(CreateVoiceNoteRow(voiceEntry.Note));
                    break;
            }
        }

        if (latestTransfer is null)
        {
            activityEmptyState.Message = "Aún no hay transferencias";
            activityEmptyState.Glyph = FluentGlyphs.Attach;
        }
        else
        {
            activityEmptyState.Message = latestTransfer.Progress >= 100
                ? "Transferencia completada"
                : "Transferencia en curso";
            activityEmptyState.Glyph = FluentGlyphs.ForFile(latestTransfer.FileName);
        }

        messagesFlow.ResumeLayout();
        if (messagesFlow.Controls.Count > 0)
        {
            messagesFlow.ScrollControlIntoView(
                messagesFlow.Controls[messagesFlow.Controls.Count - 1]);
        }
    }



    private Control CreateMessageRow(ChatMessageView message)
    {
        var availableWidth = Math.Min(540, Math.Max(260, messagesFlow.ClientSize.Width - 150));
        using var measurementFont = Theme.BodyFont();
        var displayText = message.IsDeleted
            ? "Este mensaje fue eliminado"
            : message.Text;
        var measured = TextRenderer.MeasureText(
            displayText,
            measurementFont,
            new Size(availableWidth - 30, 1000),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        var bubbleWidth = Math.Clamp(measured.Width + 30, 150, availableWidth);
        var bubbleHeight = Math.Max(58, measured.Height + 48);

        var bubble = new Guna2Panel
        {
            Size = new Size(bubbleWidth, bubbleHeight),
            BorderRadius = Theme.CardRadius,
            FillColor = message.IsOwn ? Theme.OwnMessage : Theme.OtherMessage,
            Padding = new Padding(14, 10, 14, 8)
        };

        var sender = new Label
        {
            Location = new Point(14, 8),
            Size = new Size(bubbleWidth - 28, 17),
            Text = message.IsOwn ? "Tú" : message.Sender,
            Font = Theme.MetadataFont(),
            ForeColor = message.IsOwn ? Color.FromArgb(215, 255, 255, 255) : Theme.SecondaryText
        };
        var content = new Label
        {
            Location = new Point(14, 27),
            Size = new Size(bubbleWidth - 28, measured.Height + 4),
            Text = displayText,
            Font = message.IsDeleted ? Theme.MetadataFont() : Theme.BodyFont(),
            ForeColor = message.IsDeleted
                ? (message.IsOwn ? Color.FromArgb(180, 255, 255, 255) : Theme.SecondaryText)
                : (message.IsOwn ? Color.White : Theme.MainText)
        };
        var timeStr = message.Timestamp.ToLocalTime().ToString("HH:mm");
        if (message.IsEdited && !message.IsDeleted)
        {
            timeStr += " (editado)";
        }
        var timestamp = new Label
        {
            Location = new Point(14, bubbleHeight - 21),
            Size = new Size(bubbleWidth - 28, 16),
            Text = timeStr,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.MetadataFont(),
            ForeColor = message.IsOwn ? Color.FromArgb(190, 255, 255, 255) : Theme.SecondaryText
        };
        bubble.Controls.AddRange([sender, content, timestamp]);

        if (message.IsOwn && !message.IsDeleted)
        {
            var menu = new ContextMenuStrip();
            var editItem = new ToolStripMenuItem("Editar");
            editItem.Click += (_, _) =>
            {
                if (selectedRecipientId.HasValue)
                {
                    var newText = ShowEditMessageDialog(message.Text);
                    if (!string.IsNullOrWhiteSpace(newText) && newText != message.Text)
                    {
                        EditMessageRequested?.Invoke(
                            this,
                            new EditMessageRequestedEventArgs(selectedRecipientId.Value, message.Id, newText));
                    }
                }
            };
            var deleteItem = new ToolStripMenuItem("Eliminar");
            deleteItem.Click += (_, _) =>
            {
                if (selectedRecipientId.HasValue && MessageBox.Show("¿Seguro que deseas eliminar este mensaje?", "Confirmar eliminación", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    DeleteMessageRequested?.Invoke(
                        this,
                        new DeleteMessageRequestedEventArgs(selectedRecipientId.Value, message.Id));
                }
            };
            menu.Items.AddRange([editItem, deleteItem]);
            bubble.ContextMenuStrip = menu;
            content.ContextMenuStrip = menu;
        }

        return CreateAlignedRow(bubble, message.IsOwn, bubbleHeight + 12);
    }

    private string? ShowEditMessageDialog(string currentText)
    {
        using var dialog = new Form
        {
            Text = "Editar mensaje",
            Width = 420,
            Height = 180,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Surface
        };
        var input = new Guna2TextBox
        {
            Location = new Point(20, 20),
            Size = new Size(360, 42),
            Text = currentText
        };
        Theme.StyleTextBox(input);
        var btnSave = new Guna2Button
        {
            Text = "Guardar",
            Location = new Point(190, 80),
            Size = new Size(90, 36),
            DialogResult = DialogResult.OK
        };
        Theme.StylePrimaryButton(btnSave);
        var btnCancel = new Guna2Button
        {
            Text = "Cancelar",
            Location = new Point(290, 80),
            Size = new Size(90, 36),
            DialogResult = DialogResult.Cancel
        };
        dialog.Controls.AddRange([input, btnSave, btnCancel]);
        dialog.AcceptButton = btnSave;
        dialog.CancelButton = btnCancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }

    private Panel CreateAlignedRow(Control content, bool alignRight, int height)
    {
        var scrollMargin = messagesFlow.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        var rowWidth = Math.Max(280, messagesFlow.ClientSize.Width - scrollMargin - 48);
        var row = new Panel
        {
            Width = rowWidth,
            Height = height,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 6),
            Tag = alignRight
        };
        content.Location = new Point(alignRight ? Math.Max(0, row.Width - content.Width) : 0, 0);
        row.Controls.Add(content);
        return row;
    }

    private void RealignMessageRows()
    {
        var scrollMargin = messagesFlow.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        var rowWidth = Math.Max(280, messagesFlow.ClientSize.Width - scrollMargin - 48);
        var maxCardWidth = Math.Min(380, Math.Max(240, rowWidth - 60));

        foreach (Control row in messagesFlow.Controls)
        {
            if (ReferenceEquals(row, messagesEmptyState) || row.Controls.Count == 0)
            {
                continue;
            }

            row.Width = rowWidth;
            var alignRight = row.Tag is true;
            var content = row.Controls[0];
            if (content is FileTransferCard card)
            {
                card.Width = maxCardWidth;
            }

            content.Left = alignRight ? Math.Max(0, row.Width - content.Width) : 0;
        }

        messagesEmptyState.Width = rowWidth;
    }

    private static void SetEmptyStateColors(Control root, Color color)
    {
        root.ForeColor = color;
        foreach (Control child in root.Controls)
        {
            child.ForeColor = color;
        }
    }

    private void HandleMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Shift)
        {
            return;
        }

        e.SuppressKeyPress = true;
        HandleSendClick(sender, EventArgs.Empty);
    }

    private void HandleSendClick(object? sender, EventArgs e)
    {
        if (!selectedRecipientId.HasValue && !selectedGroupId.HasValue)
        {
            ShowComposerError("Selecciona un usuario o grupo.");
            return;
        }

        var message = messageTextBox.Text;
        if (message.Length == 0)
        {
            ShowComposerError("Escribe un mensaje antes de enviarlo.");
            return;
        }

        ClearComposerError();
        if (selectedGroupId.HasValue)
        {
            SendGroupMessageRequested?.Invoke(
                this,
                new SendGroupMessageRequestedEventArgs(selectedGroupId.Value, message));
        }
        else if (selectedRecipientId.HasValue)
        {
            SendMessageRequested?.Invoke(
                this,
                new MessageRequestedEventArgs(selectedRecipientId.Value, message));
        }
    }

    private void HandleAttachmentClick(object? sender, EventArgs e)
    {
        if (!selectedRecipientId.HasValue)
        {
            ShowComposerError("Selecciona un destinatario.");
            return;
        }

        var selectedFiles = fileSelectionService.SelectFiles(this);
        if (selectedFiles.Count == 0)
        {
            return;
        }

        ClearComposerError();
        AttachmentRequested?.Invoke(
            this,
            new AttachmentRequestedEventArgs(selectedRecipientId.Value, selectedFiles));
    }

    private void HandleMicClick(object? sender, EventArgs e)
    {
        if (!selectedRecipientId.HasValue)
        {
            ShowComposerError("Selecciona un usuario para enviar una nota de voz.");
            return;
        }

        if (!audioRecorder.IsRecording)
        {
            audioRecorder.StartRecording();
            recordStartTime = DateTime.Now;
            micButton.Text = "🔴";
            micButton.FillColor = Color.Red;
            ShowComposerError("Grabando audio... Presiona 🔴 para detener y enviar.");
        }
        else
        {
            var duration = (long)(DateTime.Now - recordStartTime).TotalMilliseconds;
            var (bytes, path) = audioRecorder.StopRecording();
            micButton.Text = "🎙️";
            Theme.StyleSecondaryButton(micButton);
            ClearComposerError();

            if (bytes.Length > 0 && selectedRecipientId.HasValue)
            {
                SendVoiceNoteRequested?.Invoke(
                    this,
                    new SendVoiceNoteRequestedEventArgs(selectedRecipientId.Value, bytes, duration));
            }
        }
    }

    public void AppendVoiceNote(byte peerId, VoiceNoteView note)
    {
        var row = CreateVoiceNoteRow(note);
        messagesFlow.Controls.Add(row);
        if (messagesFlow.Controls.Count > 0)
        {
            messagesFlow.ScrollControlIntoView(messagesFlow.Controls[messagesFlow.Controls.Count - 1]);
        }
    }

    private Control CreateVoiceNoteRow(VoiceNoteView note)
    {
        var rowWidth = Math.Min(360, Math.Max(260, messagesFlow.ClientSize.Width - 140));
        var panel = new Guna2Panel
        {
            Size = new Size(rowWidth, 64),
            BorderRadius = Theme.CardRadius,
            FillColor = note.IsOwn ? Theme.Primary : Theme.Surface,
            Margin = new Padding(4)
        };

        var playBtn = new Guna2Button
        {
            Location = new Point(8, 12),
            Size = new Size(40, 40),
            Text = "▶",
            BorderRadius = 20,
            Font = Theme.BodyFont(11F, FontStyle.Bold)
        };
        Theme.StyleSecondaryButton(playBtn);

        var trackBar = new Guna2TrackBar
        {
            Location = new Point(56, 12),
            Size = new Size(rowWidth - 105, 20),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            ThumbColor = note.IsOwn ? Color.White : Theme.Primary
        };

        var timeLabel = new Label
        {
            Location = new Point(56, 36),
            Size = new Size(160, 20),
            Text = $"00:00 / {note.DurationText}",
            Font = Theme.MetadataFont(),
            ForeColor = note.IsOwn ? Color.FromArgb(230, 255, 255, 255) : Theme.SecondaryText
        };

        var folderBtn = new Guna2Button
        {
            Location = new Point(rowWidth - 44, 14),
            Size = new Size(36, 36),
            Text = "📂",
            BorderRadius = 8
        };
        Theme.StyleSecondaryButton(folderBtn);
        folderBtn.Click += (_, _) =>
        {
            if (File.Exists(note.LocalFilePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{note.LocalFilePath}\"");
            }
            else
            {
                var dir = Path.GetDirectoryName(note.LocalFilePath);
                if (Directory.Exists(dir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                }
            }
        };

        Media.MciAudioPlayer? mciPlayer = null;
        System.Windows.Forms.Timer? playTimer = null;

        playBtn.Click += (_, _) =>
        {
            if (File.Exists(note.LocalFilePath))
            {
                mciPlayer ??= new Media.MciAudioPlayer(note.LocalFilePath);
                if (playTimer is null)
                {
                    playTimer = new System.Windows.Forms.Timer { Interval = 100 };
                    playTimer.Tick += (_, _) =>
                    {
                        if (mciPlayer is null) return;
                        var pos = mciPlayer.GetPositionMs();
                        var total = mciPlayer.GetDurationMs();
                        if (total > 0)
                        {
                            var pct = Math.Min(100, (pos * 100) / total);
                            trackBar.Value = pct;
                            var posSec = pos / 1000;
                            timeLabel.Text = $"{posSec:D2}:{(pos / 100) % 10:D1} / {note.DurationText}";
                            if (pos >= total || !mciPlayer.IsPlaying())
                            {
                                playBtn.Text = "▶";
                                playTimer.Stop();
                                trackBar.Value = 0;
                            }
                        }
                    };
                }

                if (mciPlayer.IsPlaying())
                {
                    mciPlayer.Pause();
                    playTimer.Stop();
                    playBtn.Text = "▶";
                }
                else
                {
                    mciPlayer.Play();
                    playTimer.Start();
                    playBtn.Text = "⏸️";
                }
            }
            else
            {
                Media.WaveAudioRecorder.PlayAudio(note.AudioData);
            }
        };

        panel.Controls.AddRange([playBtn, trackBar, timeLabel, folderBtn]);
        return CreateAlignedRow(panel, note.IsOwn, 76);
    }

    public void ShowIncomingCallDialog(byte callerId, Guid callId, string callerName)
    {
        var result = MessageBox.Show(
            $"Llamada de voz entrante de {callerName}.\n¿Deseas aceptar la llamada?",
            "Llamada entrante 📞",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        AnswerCallRequested?.Invoke(
            this,
            new CallAnsweredRequestedEventArgs(callerId, callId, result == DialogResult.Yes));
    }

    public void ShowActiveCallBanner(Guid callId, string peerName)
    {
        currentActiveCallId = callId;
        if (activeCallPanel is null)
        {
            activeCallPanel = new Guna2Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                FillColor = Color.FromArgb(40, 200, 80),
                Padding = new Padding(12, 4, 12, 4)
            };
            activeCallLabel = new Label
            {
                Location = new Point(16, 6),
                Size = new Size(320, 24),
                Text = $"🟢 Llamada en curso con {peerName}",
                Font = Theme.BodyFont(9F, FontStyle.Bold),
                ForeColor = Color.White
            };
            endCallButton = new Guna2Button
            {
                Location = new Point(350, 4),
                Size = new Size(90, 28),
                Text = "Colgar 🔴",
                FillColor = Color.DarkRed
            };
            endCallButton.Click += (_, _) =>
            {
                if (currentActiveCallId.HasValue && selectedRecipientId.HasValue)
                {
                    EndCallRequested?.Invoke(
                        this,
                        new EndCallRequestedEventArgs(selectedRecipientId.Value, currentActiveCallId.Value));
                }
                HideActiveCallBanner();
            };
            activeCallPanel.Controls.AddRange([activeCallLabel, endCallButton]);
            Controls.Add(activeCallPanel);
            activeCallPanel.BringToFront();
        }
        activeCallPanel.Visible = true;
    }

    public void HideActiveCallBanner()
    {
        currentActiveCallId = null;
        if (activeCallPanel is not null)
        {
            activeCallPanel.Visible = false;
        }
    }

    private sealed class ConversationState
    {
        public List<ConversationEntry> Entries { get; } = [];
    }

    private abstract class ConversationEntry;

    private sealed class MessageConversationEntry(ChatMessageView message)
        : ConversationEntry
    {
        public ChatMessageView Message { get; set; } = message;
    }

    private sealed class FileConversationEntry(FileTransferView transfer)
        : ConversationEntry
    {
        public FileTransferView Transfer { get; set; } = transfer;
    }

    private sealed class VoiceConversationEntry(VoiceNoteView note)
        : ConversationEntry
    {
        public VoiceNoteView Note { get; set; } = note;
    }
}
