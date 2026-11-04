using System;
using System.Linq;
using System.Threading.Tasks;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Service that provides global email notifications for the active user across all accessible Inboxes.
/// </summary>
public class EmailNotificationService : IAsyncDisposable
{
    private readonly IUiNotificationPresenter _presenter;
    private readonly EmailSyncStateService _syncState;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly IEmailInboxService? _inboxService;
    private readonly UserCircuitContext? _circuitContext;
    private readonly PresenceStateService? _presenceState;
    private readonly ISoundService? _soundService;
    private readonly IWebPushService? _webPush;
    private string? _currentUserId;
    private DateTimeOffset? _lastVisitedAt;

    public int TotalUnreadCount { get; private set; }
    public event Action? OnUnreadCountChanged;

    public Func<Func<Task>, Task> BackgroundTaskRunner { get; set; } = work => Task.Run(work);
    public Task? LastBackgroundTask { get; private set; }

    public EmailNotificationService(
        IUiNotificationPresenter presenter,
        EmailSyncStateService syncState,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        IEmailInboxService? inboxService = null,
        UserCircuitContext? circuitContext = null,
        PresenceStateService? presenceState = null,
        ISoundService? soundService = null,
        IWebPushService? webPush = null,
        Func<Func<Task>, Task>? backgroundTaskRunner = null)
    {
        _presenter = presenter ?? NullUiNotificationPresenter.Instance;
        _syncState = syncState;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _inboxService = inboxService;
        _circuitContext = circuitContext;
        _presenceState = presenceState;
        _soundService = soundService;
        _webPush = webPush;
        if (backgroundTaskRunner != null)
        {
            BackgroundTaskRunner = backgroundTaskRunner;
        }
    }

    private bool ShouldDisplayInAppNotification()
    {
        if (_circuitContext == null) return true;
        return _circuitContext.ShouldDisplayInAppNotification(_presenceState);
    }

    public Task InitializeAsync(string userId)
    {
        if (_currentUserId != null) return Task.CompletedTask;

        _currentUserId = userId;
        LoadUnreadCount();

        _syncState.OnNewEmailReceived += OnNewEmailReceived;
        _syncState.OnUnreadCountChanged += HandleUnreadCountChanged;

        return Task.CompletedTask;
    }

    private void HandleUnreadCountChanged(string accountId)
    {
        if (_currentUserId == null) return;
        LoadUnreadCount();
    }

    public void MarkEmailPageVisited()
    {
        if (_currentUserId == null) return;

        bool hadUnseenUnread = TotalUnreadCount > 0 || ComputeCurrentUnseenUnreadCount() > 0;

        _lastVisitedAt = GetFallbackVisitedWatermark();
        _inboxService?.MarkEmailPageVisited(_currentUserId);

        TotalUnreadCount = 0;
        OnUnreadCountChanged?.Invoke();

        if (hadUnseenUnread && _webPush != null)
        {
            var userId = _currentUserId;
            LastBackgroundTask = BackgroundTaskRunner(async () =>
            {
                try
                {
                    await _webPush.SendClearNotificationAsync(userId, $"email-{userId}");
                }
                catch
                {
                    // Best-effort cross-device clear push
                }
            });
        }
    }

    public void LoadUnreadCount()
    {
        if (_currentUserId == null) return;

        if (_presenter.IsOnEmailPage())
        {
            MarkEmailPageVisited();
            return;
        }

        TotalUnreadCount = ComputeCurrentUnseenUnreadCount();
        OnUnreadCountChanged?.Invoke();
    }

    private int ComputeCurrentUnseenUnreadCount()
    {
        if (_currentUserId == null) return 0;

        if (_inboxService != null)
        {
            return _inboxService.GetUnseenUnreadCountForUser(_currentUserId);
        }

        var folders = _emailFolders.GetByAccount(_currentUserId);
        int totalUnread = 0;
        foreach (var folder in folders)
        {
            if (folder.IsTrash || folder.IsDrafts) continue;
            totalUnread += _emailMessages.GetUnreadCount(
                _currentUserId,
                folder.Path,
                _lastVisitedAt.HasValue ? m => m.Date > _lastVisitedAt.Value : null);
        }

        return totalUnread;
    }

    private DateTimeOffset GetFallbackVisitedWatermark()
    {
        var watermark = DateTimeOffset.UtcNow;
        if (_currentUserId == null || _inboxService != null) return watermark;

        foreach (var folder in _emailFolders.GetByAccount(_currentUserId))
        {
            if (folder.IsTrash || folder.IsDrafts) continue;
            foreach (var meta in _emailMessages.GetFolderMetadata(_currentUserId, folder.Path))
            {
                if (!meta.IsRead && meta.Date > watermark)
                {
                    watermark = meta.Date;
                }
            }
        }

        return watermark;
    }

    private void OnNewEmailReceived(string accountId, EmailMessage msg)
    {
        if (_currentUserId == null) return;

        if (_inboxService != null)
        {
            var authorizedUsers = _inboxService.GetAuthorizedEmployeeIdsForMessage(msg);
            if (!authorizedUsers.Contains(_currentUserId, StringComparer.OrdinalIgnoreCase))
                return;
        }
        else if (!string.Equals(accountId, _currentUserId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LoadUnreadCount();

        if (!ShouldDisplayInAppNotification()) return;

        if (!_presenter.IsOnEmailPage())
        {
            if (_soundService != null)
            {
                _ = _soundService.PlayNotificationSoundAsync(NotificationCategories.Email);
            }
            _presenter.ShowEmailNotification(msg.FromName, msg.Subject, msg.Snippet);
        }
    }

    public ValueTask DisposeAsync()
    {
        _syncState.OnNewEmailReceived -= OnNewEmailReceived;
        _syncState.OnUnreadCountChanged -= HandleUnreadCountChanged;
        return ValueTask.CompletedTask;
    }
}
