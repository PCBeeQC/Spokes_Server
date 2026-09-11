using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Components;
using MudBlazor;
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
using Spokes_Server.Components.Shared;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Service that provides global email notifications.
/// Tracks unread inbox email count and shows snackbars when new emails arrive.
/// </summary>
public class EmailNotificationService : IUserNotificationService, IAsyncDisposable
{
    private readonly ISnackbar _snackbar;
    private readonly NavigationManager _nav;
    private readonly EmailSyncStateService _syncState;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private string? _currentUserId;

    public int TotalUnreadCount { get; private set; }
    public event Action? OnUnreadCountChanged;

    public EmailNotificationService(
        ISnackbar snackbar,
        NavigationManager nav,
        EmailSyncStateService syncState,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages)
    {
        _snackbar = snackbar;
        _nav = nav;
        _syncState = syncState;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
    }

    public async Task InitializeAsync(string userId)
    {
        if (_currentUserId != null) return; // Already initialized

        _currentUserId = userId;

        // Load initial unread count
        LoadUnreadCount();

        // Subscribe to events
        _syncState.OnNewEmailReceived += OnNewEmailReceived;
        _syncState.OnUnreadCountChanged += HandleUnreadCountChanged;

        await Task.CompletedTask;
    }

    private void HandleUnreadCountChanged(string employeeId)
    {
        if (employeeId == _currentUserId)
        {
            LoadUnreadCount();
        }
    }

    public void LoadUnreadCount()
    {
        if (_currentUserId == null) return;

        // Use the in-memory index for zero disk I/O
        var folders = _emailFolders.GetByEmployee(_currentUserId);
        int totalUnread = 0;
        foreach (var folder in folders)
        {
            if (folder.IsTrash || folder.IsDrafts) continue;
            totalUnread += _emailMessages.GetUnreadCount(_currentUserId, folder.Path);
        }

        TotalUnreadCount = totalUnread;
        OnUnreadCountChanged?.Invoke();
    }

    private void OnNewEmailReceived(string employeeId, EmailMessage msg)
    {
        if (employeeId != _currentUserId) return;

        // Update count
        LoadUnreadCount();

        // Show snackbar notification if they are not already on the email page
        var currentUri = _nav.Uri;
        if (currentUri.Contains("/email", StringComparison.OrdinalIgnoreCase)) return;

        _snackbar.Add<EmailNotificationContent>(
            new Dictionary<string, object>
            {
                { "SenderName", msg.FromName },
                { "Subject", msg.Subject },
                { "Snippet", msg.Snippet }
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
                    _nav.NavigateTo("/email");
                    return Task.CompletedTask;
                };
            });
    }

    public async ValueTask DisposeAsync()
    {
        _syncState.OnNewEmailReceived -= OnNewEmailReceived;
        _syncState.OnUnreadCountChanged -= HandleUnreadCountChanged;
        await Task.CompletedTask;
    }
}



