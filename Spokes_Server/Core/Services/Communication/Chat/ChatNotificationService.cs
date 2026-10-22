using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Security;

using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.InApp;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.UI;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Communication.Presence;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Service that provides global chat notifications.
/// Shows snackbar notifications for new messages when user is not on the chat page.
/// Tracks unread message counts per channel using persistent read state timestamps.
/// </summary>
public class ChatNotificationService : IAsyncDisposable
{
    private readonly IUiNotificationPresenter _presenter;
    private readonly ChatStateService _chatState;
    private readonly ChatReadStateRepository _readStates;
    private readonly ChatMessageRepository _messages;
    private readonly ChatChannelRepository _channels;
    private readonly EmployeeRepository _employees;
    private readonly NotificationRecipientFilter _recipientFilter;
    private readonly IWebPushService _webPush;
    private readonly IChatChannelAccessService _chatAccess;
    private readonly ISoundService _soundService;
    private readonly UserCircuitContext? _circuitContext;
    private readonly PresenceStateService? _presenceState;
    private readonly IInAppNotificationDispatcher? _inAppDispatcher;
    private readonly ScopedKeystoreService? _scopedKeystore;
    private readonly ILogger<ChatNotificationService>? _logger;
    private string? _currentUserId;
    private HashSet<string> _subscribedChannels = new();

    // In-memory cache of unread counts (calculated from persistent read states)
    // _unreadByChannel: ALL unread messages for sidebar badge (even muted channels)
    private Dictionary<string, int> _unreadByChannel = new();
    // _notifiedByChannel: only messages that should trigger a notification (nav menu badge)
    private Dictionary<string, int> _notifiedByChannel = new();
    // Tracks channels whose current read state has already been persisted and push-cleared
    private readonly HashSet<string> _clearedPushChannels = new();

    /// <summary>
    /// Total notified-only unread count for the nav menu badge.
    /// Does NOT include muted channels or filtered-out messages.
    /// </summary>
    public int TotalUnreadCount => _notifiedByChannel.Values.Sum();
    public event Action? OnUnreadCountChanged;

    /// <summary>
    /// Runner for background tasks like silent push notifications. Defaults to Task.Run, but can be overridden for synchronous or deterministic execution.
    /// </summary>
    public Func<Func<Task>, Task> BackgroundTaskRunner { get; set; } = work => Task.Run(work);

    /// <summary>
    /// Tracks the most recent background task dispatched by this service (e.g. silent push notifications).
    /// Enables deterministic awaiting in test suites and synchronization seams.
    /// </summary>
    public Task? LastBackgroundTask { get; private set; }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ChatNotificationService(
        CommunicationContext communicationContext,
        HrContext hrContext,
        IUiNotificationPresenter presenter,
        ChatStateService chatState,
        NotificationRecipientFilter recipientFilter,
        IWebPushService webPush,
        IChatChannelAccessService chatAccess,
        ISoundService soundService,
        UserCircuitContext? circuitContext = null,
        PresenceStateService? presenceState = null,
        IInAppNotificationDispatcher? inAppDispatcher = null,
        ScopedKeystoreService? scopedKeystore = null,
        ILogger<ChatNotificationService>? logger = null,
        Func<Func<Task>, Task>? backgroundTaskRunner = null)
        : this(
            presenter,
            chatState,
            communicationContext.ChatReadStates,
            communicationContext.ChatMessages,
            communicationContext.ChatChannels,
            hrContext.Employees,
            recipientFilter,
            webPush,
            chatAccess,
            soundService,
            circuitContext,
            presenceState,
            inAppDispatcher,
            scopedKeystore,
            logger,
            backgroundTaskRunner)
    {
    }

    [Obsolete("NotificationContentDecryptor is no longer needed; notifications are routed through NotificationRoutingService.")]
    public ChatNotificationService(
        CommunicationContext communicationContext,
        HrContext hrContext,
        IUiNotificationPresenter presenter,
        ChatStateService chatState,
        NotificationRecipientFilter recipientFilter,
        NotificationContentDecryptor contentDecryptor,
        IWebPushService webPush,
        IChatChannelAccessService chatAccess,
        ISoundService soundService,
        UserCircuitContext? circuitContext = null,
        PresenceStateService? presenceState = null,
        IInAppNotificationDispatcher? inAppDispatcher = null,
        ScopedKeystoreService? scopedKeystore = null,
        ILogger<ChatNotificationService>? logger = null,
        Func<Func<Task>, Task>? backgroundTaskRunner = null)
        : this(
            communicationContext,
            hrContext,
            presenter,
            chatState,
            recipientFilter,
            webPush,
            chatAccess,
            soundService,
            circuitContext,
            presenceState,
            inAppDispatcher,
            scopedKeystore,
            logger,
            backgroundTaskRunner)
    {
    }

    internal ChatNotificationService(
        IUiNotificationPresenter presenter,
        ChatStateService chatState,
        ChatReadStateRepository readStates,
        ChatMessageRepository messages,
        ChatChannelRepository channels,
        EmployeeRepository employees,
        NotificationRecipientFilter recipientFilter,
        IWebPushService webPush,
        IChatChannelAccessService chatAccess,
        ISoundService soundService,
        UserCircuitContext? circuitContext = null,
        PresenceStateService? presenceState = null,
        IInAppNotificationDispatcher? inAppDispatcher = null,
        ScopedKeystoreService? scopedKeystore = null,
        ILogger<ChatNotificationService>? logger = null,
        Func<Func<Task>, Task>? backgroundTaskRunner = null)
    {
        ArgumentNullException.ThrowIfNull(webPush);
        _presenter = presenter ?? NullUiNotificationPresenter.Instance;
        _chatState = chatState;
        _readStates = readStates;
        _messages = messages;
        _channels = channels;
        _employees = employees;
        _recipientFilter = recipientFilter;
        _webPush = webPush;
        _chatAccess = chatAccess;
        _soundService = soundService;
        _circuitContext = circuitContext;
        _presenceState = presenceState;
        _inAppDispatcher = inAppDispatcher;
        _scopedKeystore = scopedKeystore;
        _logger = logger;
        if (backgroundTaskRunner != null)
        {
            BackgroundTaskRunner = backgroundTaskRunner;
        }
    }

    [Obsolete("NotificationContentDecryptor is no longer needed.")]
    internal ChatNotificationService(
        IUiNotificationPresenter presenter,
        ChatStateService chatState,
        ChatReadStateRepository readStates,
        ChatMessageRepository messages,
        ChatChannelRepository channels,
        EmployeeRepository employees,
        NotificationRecipientFilter recipientFilter,
        NotificationContentDecryptor contentDecryptor,
        IWebPushService webPush,
        IChatChannelAccessService chatAccess,
        ISoundService soundService,
        UserCircuitContext? circuitContext = null,
        PresenceStateService? presenceState = null,
        IInAppNotificationDispatcher? inAppDispatcher = null,
        ILogger<ChatNotificationService>? logger = null,
        Func<Func<Task>, Task>? backgroundTaskRunner = null)
        : this(
            presenter,
            chatState,
            readStates,
            messages,
            channels,
            employees,
            recipientFilter,
            webPush,
            chatAccess,
            soundService,
            circuitContext,
            presenceState,
            inAppDispatcher,
            null,
            logger,
            backgroundTaskRunner)
    {
    }

    /// <summary>
    /// Checks whether in-app notifications (snackbars/audio) should be shown for this circuit.
    /// On mobile devices, notifications are strictly suppressed when the user is not actively viewing the app.
    /// </summary>
    private bool ShouldDisplayInAppNotification()
    {
        if (_circuitContext == null) return true;
        return _circuitContext.ShouldDisplayInAppNotification(_presenceState);
    }

    /// <summary>
    /// Get sidebar unread count for a channel (ALL messages, including muted).
    /// </summary>
    public int GetUnreadCount(string channelId) =>
        _unreadByChannel.TryGetValue(channelId, out var count) ? count : 0;

    /// <summary>
    /// Get notified unread count for a channel (only messages that triggered notifications).
    /// </summary>
    public int GetNotifiedUnreadCount(string channelId) =>
        _notifiedByChannel.TryGetValue(channelId, out var count) ? count : 0;

    public Task InitializeAsync(string userId) => InitializeAsync(userId, null);

    public Task InitializeAsync(string userId, IEnumerable<string>? channelIds)
    {
        if (_currentUserId != null) return Task.CompletedTask; // Already initialized

        _currentUserId = userId;

        if (channelIds != null)
        {
            _subscribedChannels = new HashSet<string>(channelIds);
        }
        else
        {
            var employee = _employees.GetById(userId);
            if (employee != null)
            {
                var channels = _chatAccess.GetChannelsForUser(userId);
                
                if (!employee.HasPermission(Spokes_Server.Core.Constants.AppPermissions.Chat.ViewArchive))
                {
                    channels = channels.Where(c => !c.IsArchived).ToList();
                }

                _subscribedChannels = new HashSet<string>(channels.Select(c => c.Id));
            }
            else
            {
                _subscribedChannels = new HashSet<string>();
            }
        }

        // Load unread counts from persistent storage
        ReloadUnreadCounts();

        // Subscribe to local events
        _chatState.MessageReceived += OnMessageReceived;
        _chatState.MessageEdited += OnMessageEdited;
        _chatState.ModerationAlertReceived += OnModerationAlertReceived;
        if (_inAppDispatcher != null)
        {
            _inAppDispatcher.OnNotificationDispatched += HandleInAppNotification;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Load unread counts for all subscribed channels based on persistent read state timestamps.
    /// </summary>
    public void ReloadUnreadCounts()
    {
        if (_currentUserId == null) return;

        _unreadByChannel.Clear();
        _notifiedByChannel.Clear();

        foreach (var channelId in _subscribedChannels)
        {
            var lastReadAt = _readStates.GetLastReadAt(_currentUserId, channelId);

            // Fast path: get unread count strictly from in-memory index without loading messages from disk
            var unreadCount = _messages.GetUnreadCountSince(channelId, lastReadAt, _currentUserId);

            if (unreadCount > 0)
            {
                _clearedPushChannels.Remove(channelId);

                // Always increment sidebar unread count (even muted channels)
                _unreadByChannel[channelId] = unreadCount;

                // Evaluate notification tier efficiently
                var channel = _channels.GetById(channelId);
                var notifLevel = _readStates.GetNotificationLevel(
                    _currentUserId, channelId, channel?.ChannelType.ToString(), channel?.IsVoiceChannel ?? false);

                if (notifLevel == ChatNotificationLevel.None)
                {
                    // Muted: 0 notified
                }
                else if (notifLevel != ChatNotificationLevel.Mentions)
                {
                    // "All": all unread messages trigger notification without loading bodies
                    _notifiedByChannel[channelId] = unreadCount;
                }
                else
                {
                    // "Mentions": inspect messages for specific mentions
                    var unreadMessages = _messages.GetUnreadMessagesSince(channelId, lastReadAt, _currentUserId);
                    int notifiedCount = 0;
                    foreach (var msg in unreadMessages)
                    {
                        if (ShouldNotifyForMessage(msg))
                        {
                            notifiedCount++;
                        }
                    }

                    if (notifiedCount > 0)
                    {
                        _notifiedByChannel[channelId] = notifiedCount;
                    }
                }
            }
        }

        OnUnreadCountChanged?.Invoke();
    }

    private void OnMessageReceived(ChatMessage msg)
    {
        if (string.IsNullOrEmpty(_currentUserId)) return;

        // Don't notify for own messages
        if (msg.SenderId == _currentUserId) return;

        // Auto-subscribe to channels we now have access to (e.g. newly created group chats)
        if (!_subscribedChannels.Contains(msg.ChannelId))
        {
            var myChannels = _chatAccess.GetChannelsForUser(_currentUserId);
            if (myChannels.Any(c => c.Id == msg.ChannelId))
            {
                _subscribedChannels.Add(msg.ChannelId);
            }
            else
            {
                return; // User truly does not have access
            }
        }

        // A new message arrived in this channel, so its read/push-cleared state is no longer current
        _clearedPushChannels.Remove(msg.ChannelId);

        // Don't count if user is on the chat page for this channel AND actively viewing
        var isOnThisChannel = _presenter.IsOnChatChannel(msg.ChannelId);
        bool isActivelyViewing = _circuitContext?.IsActivelyViewed == true;
        if (isOnThisChannel && isActivelyViewing) return;

        // Always increment sidebar unread count (even muted channels)
        if (!_unreadByChannel.ContainsKey(msg.ChannelId))
            _unreadByChannel[msg.ChannelId] = 0;
        _unreadByChannel[msg.ChannelId]++;

        // Increment the nav-menu notified count
        if (ShouldNotifyForMessage(msg))
        {
            if (!_notifiedByChannel.ContainsKey(msg.ChannelId))
                _notifiedByChannel[msg.ChannelId] = 0;
            _notifiedByChannel[msg.ChannelId]++;
        }

        OnUnreadCountChanged?.Invoke();
    }

    /// <summary>
    /// Check if a message should trigger a notification based on the user's preference.
    /// </summary>
    private bool ShouldNotifyForMessage(ChatMessage msg)
    {
        if (_currentUserId == null) return true;

        var channel = _channels.GetById(msg.ChannelId);
        var notifLevel = _readStates.GetNotificationLevel(
            _currentUserId, msg.ChannelId, channel?.ChannelType.ToString(), channel?.IsVoiceChannel ?? false);

        if (notifLevel == ChatNotificationLevel.None)
            return false;

        if (notifLevel == ChatNotificationLevel.Mentions)
        {
            var user = _employees.GetById(_currentUserId);
            return _recipientFilter.IsUserMentioned(_currentUserId, user, msg);
        }

        return true; // "All"
    }

    private void OnModerationAlertReceived(ReportedMessage report)
    {
        if (_currentUserId == null) return;

        var employee = _employees.GetById(_currentUserId);
        if (employee != null && employee.IsActive && employee.HasPermission(Spokes_Server.Core.Constants.AppPermissions.Chat.Moderator) && employee.ModerationNotificationsEnabled)
        {
            // Only show snackbar if they didn't report it themselves
            if (report.ReporterId == _currentUserId) return;
            if (!ShouldDisplayInAppNotification()) return;

            var reporter = _employees.GetById(report.ReporterId);
            var reporterName = reporter?.FullName ?? "Someone";

            _presenter.ShowModerationAlert(reporterName, report.Reason);
        }
    }

    public Task SubscribeToChannelAsync(string channelId)
    {
        if (_subscribedChannels.Contains(channelId)) return Task.CompletedTask;
        _subscribedChannels.Add(channelId);

        // Calculate unread count for newly subscribed channel
        if (_currentUserId != null)
        {
            var lastReadAt = _readStates.GetLastReadAt(_currentUserId, channelId);
            var unreadCount = _messages.GetUnreadCountSince(channelId, lastReadAt);

            if (unreadCount > 0)
            {
                _unreadByChannel[channelId] = unreadCount;
                OnUnreadCountChanged?.Invoke();
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkChannelAsReadAsync(string channelId)
    {
        if (_currentUserId == null) return Task.CompletedTask;

        var hasCachedUnread = (_unreadByChannel.TryGetValue(channelId, out var u) && u > 0) ||
                              (_notifiedByChannel.TryGetValue(channelId, out var n) && n > 0);
        var lastReadAt = _readStates.GetLastReadAt(_currentUserId, channelId);
        var hasPersistedUnread = lastReadAt == DateTime.MinValue ||
                                 _messages.GetUnreadCountSince(channelId, lastReadAt, _currentUserId) > 0;
        var isFirstClearForState = _clearedPushChannels.Add(channelId);

        if (!hasCachedUnread && !hasPersistedUnread && !isFirstClearForState)
        {
            return Task.CompletedTask;
        }

        // Persist the read state
        var channel = _channels.GetById(channelId);
        _readStates.MarkAsRead(_currentUserId, channelId, channel?.ChannelType.ToString(), channel?.IsVoiceChannel ?? false);

        // Broadcast silent push to clear notifications on other devices in background
        LastBackgroundTask = BackgroundTaskRunner(async () =>
        {
            try
            {
                await _webPush.SendClearNotificationAsync(_currentUserId, channelId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ChatNotificationService] Silent push clear error: {Message}", ex.Message);
            }
        });

        // Update both in-memory caches
        var changed = false;
        if (_unreadByChannel.ContainsKey(channelId) && _unreadByChannel[channelId] > 0)
        {
            _unreadByChannel[channelId] = 0;
            changed = true;
        }
        if (_notifiedByChannel.ContainsKey(channelId) && _notifiedByChannel[channelId] > 0)
        {
            _notifiedByChannel[channelId] = 0;
            changed = true;
        }
        if (changed) OnUnreadCountChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task MarkAsReadAsync()
    {
        if (_currentUserId == null) return Task.CompletedTask;

        // Persist read state for all channels
        foreach (var channelId in _subscribedChannels)
        {
            _clearedPushChannels.Add(channelId);
            var channel = _channels.GetById(channelId);
            _readStates.MarkAsRead(_currentUserId, channelId, channel?.ChannelType.ToString(), channel?.IsVoiceChannel ?? false);
        }

        var channelsToClear = _subscribedChannels.ToList();
        var userId = _currentUserId;
        LastBackgroundTask = BackgroundTaskRunner(async () =>
        {
            foreach (var channelId in channelsToClear)
            {
                try
                {
                    await _webPush.SendClearNotificationAsync(userId, channelId);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[ChatNotificationService] Silent push clear error: {Message}", ex.Message);
                }
            }
        });

        // Clear both in-memory caches
        var hadUnread = _unreadByChannel.Any(x => x.Value > 0) || _notifiedByChannel.Any(x => x.Value > 0);
        _unreadByChannel.Clear();
        _notifiedByChannel.Clear();
        if (hadUnread) OnUnreadCountChanged?.Invoke();
        return Task.CompletedTask;
    }

    private void OnMessageEdited(ChatMessage msg)
    {
        if (msg.MessageType == ChatMessageType.CallInvite && msg.CallStatus != CallStatus.Active)
        {
            _ = _soundService.StopCallRingtoneAsync();
        }
    }

    private void HandleInAppNotification(InAppNotification notif)
    {
        if (string.IsNullOrEmpty(_currentUserId) || notif.RecipientUserId != _currentUserId) return;

        // Don't show snackbar if user is on the chat page for this channel AND actively viewing
        var isOnThisChannel = _presenter.IsOnChatChannel(notif.ChannelId);
        bool isActivelyViewing = _circuitContext?.IsActivelyViewed == true;
        if (isOnThisChannel && isActivelyViewing) return;

        // Only play in-app sound and display in-app snackbar if the user is actively viewing the app / circuit context permits
        if (ShouldDisplayInAppNotification())
        {
            if (!notif.IsSilent)
            {
                _ = _soundService.PlayNotificationSoundAsync(notif.Category);
            }

            var displayNotif = notif;
            if (notif.IsEncrypted && _scopedKeystore?.IsUnlocked != true)
            {
                displayNotif = new InAppNotification
                {
                    RecipientUserId = notif.RecipientUserId,
                    Title = notif.Title,
                    Content = notif.MaskedContent ?? "🔒 New Secure Message",
                    SenderName = notif.SenderName,
                    SenderAvatarUrl = notif.SenderAvatarUrl,
                    Initials = notif.Initials,
                    ChannelId = notif.ChannelId,
                    ChannelName = notif.ChannelName,
                    ServerName = notif.ServerName,
                    IsReaction = notif.IsReaction,
                    IsGroupChat = notif.IsGroupChat,
                    Category = notif.Category,
                    IsSilent = notif.IsSilent,
                    IsEncrypted = true,
                    MaskedContent = notif.MaskedContent
                };
            }

            _presenter.ShowChatNotification(displayNotif);
        }
    }

    public ValueTask DisposeAsync()
    {
        _chatState.MessageReceived -= OnMessageReceived;
        _chatState.MessageEdited -= OnMessageEdited;
        _chatState.ModerationAlertReceived -= OnModerationAlertReceived;
        if (_inAppDispatcher != null)
        {
            _inAppDispatcher.OnNotificationDispatched -= HandleInAppNotification;
        }
        return ValueTask.CompletedTask;
    }
}
