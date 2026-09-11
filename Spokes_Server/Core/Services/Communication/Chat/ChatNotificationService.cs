using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Service that provides global chat notifications.
/// Shows snackbar notifications for new messages when user is not on the chat page.
/// Tracks unread message counts per channel using persistent read state timestamps.
/// </summary>
public class ChatNotificationService : IUserNotificationService, IAsyncDisposable
{
    private readonly ISnackbar _snackbar;
    private readonly NavigationManager _nav;
    private readonly ChatStateService _chatState;
    private readonly ChatReadStateRepository _readStates;
    private readonly ChatMessageRepository _messages;
    private readonly ChatChannelRepository _channels;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly ProjectRepository _projects;
    private readonly GlobalKeystoreService _keystore;
    private readonly ICryptoService _crypto;
    private readonly IWebPushService _webPush;
    private readonly ChatService _chatService;
    private string? _currentUserId;
    private HashSet<string> _subscribedChannels = new();

    // In-memory cache of unread counts (calculated from persistent read states)
    // _unreadByChannel: ALL unread messages for sidebar badge (even muted channels)
    private Dictionary<string, int> _unreadByChannel = new();
    // _notifiedByChannel: only messages that should trigger a notification (nav menu badge)
    private Dictionary<string, int> _notifiedByChannel = new();

    /// <summary>
    /// Total notified-only unread count for the nav menu badge.
    /// Does NOT include muted channels or filtered-out messages.
    /// </summary>
    public int TotalUnreadCount => _notifiedByChannel.Values.Sum();
    public event Action? OnUnreadCountChanged;

    public ChatNotificationService(
        ISnackbar snackbar,
        NavigationManager nav,
        ChatStateService chatState,
        ChatReadStateRepository readStates,
        ChatMessageRepository messages,
        ChatChannelRepository channels,
        EmployeeRepository employees,
        TeamRepository teams,
        ProjectRepository projects,
        GlobalKeystoreService keystore,
        ICryptoService crypto,
        IWebPushService webPush,
        ChatService chatService)
    {
        _snackbar = snackbar;
        _nav = nav;
        _chatState = chatState;
        _readStates = readStates;
        _messages = messages;
        _channels = channels;
        _employees = employees;
        _teams = teams;
        _projects = projects;
        _keystore = keystore;
        _crypto = crypto;
        _webPush = webPush;
        _chatService = chatService;
    }

    /// <summary>
    /// Get sidebar unread count for a channel (ALL messages, including muted).
    /// </summary>
    public int GetUnreadCount(string channelId)
    {
        return _unreadByChannel.TryGetValue(channelId, out var count) ? count : 0;
    }

    /// <summary>
    /// Get notified unread count for a channel (only messages that triggered notifications).
    /// </summary>
    public int GetNotifiedUnreadCount(string channelId)
    {
        return _notifiedByChannel.TryGetValue(channelId, out var count) ? count : 0;
    }

    public Task InitializeAsync(string userId) => InitializeAsync(userId, null);

    public async Task InitializeAsync(string userId, IEnumerable<string>? channelIds)
    {
        if (_currentUserId != null) return; // Already initialized

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
                var channels = _chatService.GetChannelsForUser(userId);
                
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
        _chatState.ModerationAlertReceived += OnModerationAlertReceived;

        await Task.CompletedTask;
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
                // Always increment sidebar unread count (even muted channels)
                _unreadByChannel[channelId] = unreadCount;

                // Evaluate notification tier efficiently
                var channel = _channels.GetById(channelId);
                var notifLevel = _readStates.GetNotificationLevel(
                    _currentUserId, channelId, channel?.ChannelType.ToString(), channel?.IsVoiceChannel ?? false);

                if (notifLevel == "None")
                {
                    // Muted: 0 notified
                }
                else if (notifLevel != "Mentions")
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
        // Don't notify for own messages
        if (msg.SenderId == _currentUserId) return;

        // Auto-subscribe to channels we now have access to (e.g. newly created group chats)
        if (!_subscribedChannels.Contains(msg.ChannelId))
        {
            var myChannels = _chatService.GetChannelsForUser(_currentUserId);
            if (myChannels.Any(c => c.Id == msg.ChannelId))
            {
                _subscribedChannels.Add(msg.ChannelId);
            }
            else
            {
                return; // User truly does not have access
            }
        }

        // Don't count if user is on the chat page for this channel
        var currentUri = _nav.Uri;
        var isOnThisChannel = currentUri.Contains($"/chat/{msg.ChannelId}");
        if (isOnThisChannel) return;

        // Always increment sidebar unread count (even muted channels)
        if (!_unreadByChannel.ContainsKey(msg.ChannelId))
            _unreadByChannel[msg.ChannelId] = 0;
        _unreadByChannel[msg.ChannelId]++;

        // Determine whether this message should trigger a notification
        var shouldNotify = ShouldNotifyForMessage(msg);

        if (shouldNotify)
        {
            // Increment the nav-menu notified count
            if (!_notifiedByChannel.ContainsKey(msg.ChannelId))
                _notifiedByChannel[msg.ChannelId] = 0;
            _notifiedByChannel[msg.ChannelId]++;

            // Show snackbar notification
            ShowSnackbarNotification(msg);
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

        if (notifLevel == "None")
            return false;

        if (notifLevel == "Mentions")
        {
            var isMentioned = msg.MentionedUserIds.Contains(_currentUserId) || msg.MentionsEveryone;

            if (!isMentioned && msg.MentionsTeam)
            {
                var user = _employees.GetById(_currentUserId);
                // Check if user is a team member OR a team leader
                var isOnTeam = !string.IsNullOrEmpty(user?.TeamId)
                    || _teams.GetAll().Any(t => t.LeaderId == _currentUserId);
                if (isOnTeam)
                    isMentioned = true;
            }

            return isMentioned;
        }

        return true; // "All"
    }

    private void ShowSnackbarNotification(ChatMessage msg)
    {
        var sender = _employees.GetById(msg.SenderId);
        var senderName = sender?.FullName ?? "Unknown";

        var channel = _channels.GetById(msg.ChannelId);
        var channelName = channel?.Name ?? "Unknown Channel";

        if (channel?.ChannelType == ChatChannelType.Direct && channel.ParticipantIds.Count == 2)
            channelName = $"DM with {senderName}";

        var initials = GetInitials(senderName);

        var messageContent = msg.Content;
        if (msg.IsEncrypted)
        {
            messageContent = "🔒 New Secure Message";
            if (_currentUserId != null && _keystore.IsUserUnlocked(_currentUserId))
            {
                var privateKey = _keystore.GetPrivateKey(_currentUserId);
                if (privateKey != null && channel != null && channel.EncryptedChannelKeys.TryGetValue(_currentUserId, out var cipherKey))
                {
                    try
                    {
                        var channelKey = _crypto.DecryptRsa(cipherKey, privateKey);
                        messageContent = _crypto.DecryptAes(msg.Content, channelKey);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ChatNotificationService] Decryption failed: {ex.Message}");
                    }
                }
            }
        }

        var truncatedContent = string.IsNullOrEmpty(messageContent)
            ? "[Attachment]"
            : messageContent;
        truncatedContent = System.Text.RegularExpressions.Regex.Replace(truncatedContent, @"!\[gif\]\([^)]+\)|\[gif\]", "Sent a GIF", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        truncatedContent = truncatedContent.Length > 40 ? truncatedContent.Substring(0, 37) + "..." : truncatedContent;

        _snackbar.Add<ChatNotificationContent>(
            new Dictionary<string, object>
            {
                { "SenderName", senderName },
                { "Content", truncatedContent },
                { "Initials", initials },
                { "ChannelId", msg.ChannelId },
                { "ChannelName", channelName }
            },
            Severity.Normal,
            config =>
            {
                config.VisibleStateDuration = 5000;
                config.ShowCloseIcon = true;
                config.SnackbarVariant = Variant.Filled;
                config.HideIcon = true;
                config.OnClick = _ =>
                {
                    _nav.NavigateTo($"/chat/{msg.ChannelId}");
                    return Task.CompletedTask;
                };
            });
    }

    private void OnModerationAlertReceived(ReportedMessage report)
    {
        if (_currentUserId == null) return;

        var employee = _employees.GetById(_currentUserId);
        if (employee != null && employee.IsActive && employee.HasPermission(Spokes_Server.Core.Constants.AppPermissions.Chat.Moderator) && employee.ModerationNotificationsEnabled)
        {
            // Only show snackbar if they didn't report it themselves
            if (report.ReporterId == _currentUserId) return;

            var reporter = _employees.GetById(report.ReporterId);
            var reporterName = reporter?.FullName ?? "Someone";

            _snackbar.Add(
                $"{reporterName} reported a message for: {report.Reason}",
                Severity.Warning,
                config =>
                {
                    config.VisibleStateDuration = 10000;
                    config.ShowCloseIcon = true;
                    config.Action = "Review";
                    config.ActionColor = Color.Warning;
                    config.OnClick = _ =>
                    {
                        _nav.NavigateTo("/admin/moderation");
                        return Task.CompletedTask;
                    };
                }
            );
        }
    }

    private string GetInitials(string name)
    {
        if (string.IsNullOrEmpty(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return $"{parts[0][0]}{parts[1][0]}".ToUpper();
        }
        return name.Length >= 2 ? name.Substring(0, 2).ToUpper() : name.ToUpper();
    }

    public async Task SubscribeToChannel(string channelId)
    {
        if (_subscribedChannels.Contains(channelId)) return;
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

        await Task.CompletedTask;
    }

    public async Task MarkChannelAsReadAsync(string channelId)
    {
        if (_currentUserId == null) return;

        // Persist the read state
        var channel = _channels.GetById(channelId);
        _readStates.MarkAsRead(_currentUserId, channelId, channel?.ChannelType.ToString(), channel?.IsVoiceChannel ?? false);

        // Broadcast silent push to clear notifications on other devices in background
        if (_webPush != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _webPush.SendClearNotificationAsync(_currentUserId, channelId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ChatNotificationService] Silent push clear error: {ex.Message}");
                }
            });
        }

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
    }

    public async Task MarkAsReadAsync()
    {
        if (_currentUserId == null) return;

        // Persist read state for all channels
        foreach (var channelId in _subscribedChannels)
        {
            var channel = _channels.GetById(channelId);
            _readStates.MarkAsRead(_currentUserId, channelId, channel?.ChannelType.ToString(), channel?.IsVoiceChannel ?? false);
        }

        if (_webPush != null)
        {
            var channelsToClear = _subscribedChannels.ToList();
            var userId = _currentUserId;
            _ = Task.Run(async () =>
            {
                foreach (var channelId in channelsToClear)
                {
                    try
                    {
                        await _webPush.SendClearNotificationAsync(userId, channelId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ChatNotificationService] Silent push clear error: {ex.Message}");
                    }
                }
            });
        }

        // Clear both in-memory caches
        var hadUnread = _unreadByChannel.Any(x => x.Value > 0) || _notifiedByChannel.Any(x => x.Value > 0);
        _unreadByChannel.Clear();
        _notifiedByChannel.Clear();
        if (hadUnread) OnUnreadCountChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _chatState.MessageReceived -= OnMessageReceived;
        _chatState.ModerationAlertReceived -= OnModerationAlertReceived;
        await Task.CompletedTask;
    }
}



