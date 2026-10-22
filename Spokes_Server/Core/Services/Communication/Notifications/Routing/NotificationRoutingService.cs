using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications.InApp;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

/// <summary>
/// Centralized routing engine for chat, email, and moderation notifications.
/// Evaluates channel access, user notification preferences, and presence tiers
/// to dispatch immediate pushes, delayed mobile pushes, and local snackbars.
/// </summary>
public class NotificationRoutingService : INotificationRoutingService
{
    private readonly EmployeeRepository _employees;
    private readonly ProjectRepository _projects;
    private readonly ChatReadStateRepository _readStates;
    private readonly TeamRepository _teams;
    private readonly IWebPushService _webPush;
    private readonly NotificationQueueService _queue;
    private readonly GlobalKeystoreService _keystore;
    private readonly ICryptoService _crypto;
    private readonly PushTokenService? _pushTokenService;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly ChatChannelRepository _chatChannels;
    private readonly ChatMessageRepository _chatMessages;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly ILogger<NotificationRoutingService> _logger;
    private readonly IChatChannelAccessService _chatAccess;

    private readonly NotificationRecipientFilter _recipientFilter;
    private readonly NotificationContentDecryptor _contentDecryptor;
    private readonly NotificationRateLimiter _rateLimiter;
    private readonly IPushDecisionService? _pushDecision;
    private readonly IInAppNotificationDispatcher _inAppDispatcher;

    [ActivatorUtilitiesConstructor]
    public NotificationRoutingService(
        CommunicationContext communicationContext,
        HrContext hrContext,
        ProjectsContext projectsContext,
        CoreContext coreContext,
        IWebPushService webPush,
        NotificationQueueService queue,
        GlobalKeystoreService keystore,
        ICryptoService crypto,
        IDataProtectionProvider? dataProtection = null,
        IChatChannelAccessService chatAccess = null!,
        NotificationRecipientFilter? recipientFilter = null,
        NotificationContentDecryptor? contentDecryptor = null,
        NotificationRateLimiter? rateLimiter = null,
        ILogger<NotificationRoutingService>? logger = null,
        PushTokenService? pushTokenService = null,
        IPushDecisionService? pushDecision = null,
        IInAppNotificationDispatcher? inAppDispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(communicationContext);
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(projectsContext);
        ArgumentNullException.ThrowIfNull(coreContext);

        _employees = hrContext.Employees;
        _projects = projectsContext.Projects;
        _readStates = communicationContext.ChatReadStates;
        _teams = hrContext.Teams;
        _webPush = webPush;
        _queue = queue;
        _keystore = keystore;
        _crypto = crypto;
        _pushTokenService = pushTokenService ?? (dataProtection != null ? new PushTokenService(dataProtection) : null);
        _emailFolders = communicationContext.EmailFolders;
        _emailMessages = communicationContext.EmailMessages;
        _chatChannels = communicationContext.ChatChannels;
        _chatMessages = communicationContext.ChatMessages;
        _companyProfile = coreContext.CompanyProfile;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationRoutingService>.Instance;
        _chatAccess = chatAccess;
        _recipientFilter = recipientFilter ?? new NotificationRecipientFilter(_readStates, _chatMessages);
        _contentDecryptor = contentDecryptor ?? new NotificationContentDecryptor(_keystore, _crypto);
        _rateLimiter = rateLimiter ?? new NotificationRateLimiter(_readStates, _chatMessages);
        _pushDecision = pushDecision;
        _inAppDispatcher = inAppDispatcher ?? new InAppNotificationDispatcher();
    }

    public virtual int GetTotalBadgeCount(string userId)
    {
        int total = 0;

        // 1. Email Unread (using folder cache / index metadata)
        var folders = _emailFolders.GetByEmployee(userId);
        foreach (var folder in folders)
        {
            if (folder.IsTrash || folder.IsDrafts) continue;
            var unread = folder.UnreadCount > 0
                ? folder.UnreadCount
                : _emailMessages.GetUnreadCount(userId, folder.Path);
            total += unread;
        }

        // 2. Chat Notified Unread
        var channels = _chatAccess.GetChannelsForUser(userId).Where(c => !c.IsArchived).ToList();
        var employee = _employees.GetById(userId);
        foreach (var channel in channels)
        {
            var notifLevel = _readStates.GetNotificationLevel(userId, channel.Id, channel.ChannelType.ToString(), channel.IsVoiceChannel);
            if (notifLevel == ChatNotificationLevel.None) continue;

            var lastReadAt = _readStates.GetLastReadAt(userId, channel.Id);
            if (notifLevel == ChatNotificationLevel.All)
            {
                // Fast in-memory index count without loading message JSONs from disk
                total += _chatMessages.GetUnreadCountSince(channel.Id, lastReadAt, userId);
            }
            else if (notifLevel == ChatNotificationLevel.Mentions)
            {
                var unreadMessages = _chatMessages.GetUnreadMessagesSince(channel.Id, lastReadAt, userId);
                var channelIndex = _chatMessages.GetIndexForChannel(channel.Id);
                foreach (var msg in unreadMessages)
                {
                    if (_recipientFilter.IsUserMentioned(userId, employee, msg, channelIndex))
                    {
                        total++;
                    }
                }
            }
        }

        return total;
    }

    public virtual BadgeBreakdownResult GetBadgeBreakdown(string userId)
    {
        int total = 0;
        var breakdown = new Dictionary<string, int>();

        // 1. Email Unread
        var folders = _emailFolders.GetByEmployee(userId);
        foreach (var folder in folders)
        {
            if (folder.IsTrash || folder.IsDrafts) continue;
            var unreadCount = folder.UnreadCount > 0
                ? folder.UnreadCount
                : _emailMessages.GetUnreadCount(userId, folder.Path);
            if (unreadCount > 0)
            {
                total += unreadCount;
                breakdown[$"Email_{folder.Path}"] = unreadCount;
            }
        }

        // 2. Chat Notified Unread
        var channels = _chatAccess.GetChannelsForUser(userId).Where(c => !c.IsArchived).ToList();
        var employee = _employees.GetById(userId);
        foreach (var channel in channels)
        {
            var notifLevel = _readStates.GetNotificationLevel(userId, channel.Id, channel.ChannelType.ToString(), channel.IsVoiceChannel);
            if (notifLevel == ChatNotificationLevel.None) continue;

            var lastReadAt = _readStates.GetLastReadAt(userId, channel.Id);
            int channelTotal = 0;
            if (notifLevel == ChatNotificationLevel.All)
            {
                // Fast in-memory index count without loading message JSONs from disk
                channelTotal = _chatMessages.GetUnreadCountSince(channel.Id, lastReadAt, userId);
            }
            else if (notifLevel == ChatNotificationLevel.Mentions)
            {
                var unreadMessages = _chatMessages.GetUnreadMessagesSince(channel.Id, lastReadAt, userId);
                var channelIndex = _chatMessages.GetIndexForChannel(channel.Id);
                foreach (var msg in unreadMessages)
                {
                    if (_recipientFilter.IsUserMentioned(userId, employee, msg, channelIndex))
                    {
                        channelTotal++;
                    }
                }
            }

            if (channelTotal > 0)
            {
                total += channelTotal;
                var channelName = !string.IsNullOrEmpty(channel.Name) ? channel.Name : channel.Id;
                breakdown[$"Chat_{channelName}"] = channelTotal;
            }
        }

        return new BadgeBreakdownResult { Count = total, Breakdown = breakdown };
    }

    /// <summary>
    /// Evaluates recipients and routes notifications to both in-app circuits (snackbars/audio) and WebPush.
    /// </summary>
    public async Task RouteChatNotificationAsync(ChatMessage message, ChatChannel channel, Employee sender)
    {
        var recipientIds = _chatAccess.GetUsersForChannel(channel.Id).Where(id => id != sender.Id).ToHashSet();
        var serverName = _companyProfile.Get()?.CompanyName ?? "Spokes";
        var isGroupChat = channel.ChannelType != ChatChannelType.Direct;
        var channelName = isGroupChat ? channel.Name : null;

        var tasks = recipientIds.Select(async userId =>
        {
            var employee = _employees.GetById(userId);
            if (!_recipientFilter.ShouldNotifyChat(employee, sender, channel, message))
            {
                return;
            }

            bool isSilent = _rateLimiter.ShouldSuppressSound(employee!, channel.Id);
            var title = $"{sender.FirstName} {sender.LastName}".Trim();
            var body = _contentDecryptor.DecryptAndFormatContent(message, channel, userId, 100);
            var maskedBody = message.IsEncrypted ? "🔒 New Secure Message" : null;
            var url = $"/chat/{channel.Id}";
            var icon = GenerateAvatarUrl(sender.Id);
            var tag = $"chat-{channel.Id}";

            bool isCall = message.MessageType == ChatMessageType.CallInvite;
            string category = isCall ? NotificationCategories.Call : NotificationCategories.Chat;

            // 1. Dispatch In-App Notification (Rich Snackbar + Audio) to connected circuits
            if (_inAppDispatcher != null)
            {
                var formattedChannelName = channel.ChannelType == ChatChannelType.Direct && channel.ParticipantIds.Count == 2
                    ? $"DM with {sender.FullName}"
                    : (channel.Name ?? "Unknown Channel");

                _inAppDispatcher.Dispatch(new InAppNotification
                {
                    RecipientUserId = userId,
                    Title = sender.FullName,
                    Content = body,
                    SenderName = sender.FullName,
                    SenderAvatarUrl = icon,
                    Initials = GetInitials(sender.FullName),
                    ChannelId = channel.Id,
                    ChannelName = formattedChannelName,
                    ServerName = serverName,
                    IsReaction = false,
                    IsGroupChat = isGroupChat,
                    Category = category,
                    IsSilent = isSilent,
                    IsEncrypted = message.IsEncrypted,
                    MaskedContent = maskedBody
                });
            }

            // 2. Dispatch Push Notification only if user is away/offline and has push enabled
            var tier = _pushDecision?.DetermineNotificationTier(userId) ?? PresenceTier.All;
            if (tier == PresenceTier.None)
            {
                return;
            }

            if (!employee!.PushNotificationsEnabled)
            {
                return;
            }

            var userSoundChoice = employee.GetNotificationSound(category);
            var sound = NotificationSoundCatalog.GetEffectivePushSound(category, userSoundChoice);

            var actions = new object[]
            {
                new { action = "open", title = "Open Chat" }
            };

            var badgeCount = GetTotalBadgeCount(userId);

            var request = new PushNotificationRequest
            {
                UserId = userId,
                Title = title,
                Body = body,
                Url = url,
                Icon = icon,
                Tier = tier,
                Tag = tag,
                Actions = actions,
                Category = category,
                ThreadId = channel.Id,
                ServerName = serverName,
                ChannelName = channelName,
                IsGroupChat = isGroupChat,
                Badge = badgeCount,
                IsSilent = isSilent,
                Sound = sound,
                IsEncrypted = message.IsEncrypted,
                MaskedBody = maskedBody
            };

            await _webPush.SendNotificationAsync(request);

            if (tier == PresenceTier.DesktopOnly)
            {
                // Queue a Delayed Mobile Push to fire in 1 minute if they don't read it
                _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification(request, message.Id, channel.Id, "Chat", TimeSpan.FromMinutes(1)));
            }
        });

        await Task.WhenAll(tasks);
    }

    public async Task ClearChannelNotificationsAsync(string channelId)
    {
        var participantIds = _chatAccess.GetUsersForChannel(channelId);
        foreach (var userId in participantIds)
        {
            try
            {
                await _webPush.SendClearNotificationAsync(userId, channelId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send clear notification for channel {ChannelId} to user {UserId}", channelId, userId);
            }
        }
    }

    public async Task RouteReactionNotificationAsync(ChatMessage message, ChatChannel channel, string emoji, Employee reactionSender)
    {
        if (message.SenderId == reactionSender.Id) return;

        var originalSender = _employees.GetById(message.SenderId);
        if (!_recipientFilter.ShouldNotifyReaction(originalSender, reactionSender)) return;

        bool isSilent = _rateLimiter.ShouldSuppressSound(originalSender, channel.Id);

        var truncatedMessage = _contentDecryptor.DecryptAndFormatContent(message, channel, originalSender.Id, 40, isReaction: true);
        var maskedBody = message.IsEncrypted ? "🔒 Secure Message" : null;
        var title = $"{reactionSender.FirstName} reacted {emoji} to:";
        var body = $"{truncatedMessage}";

        var url = $"/chat/{channel.Id}";
        var icon = GenerateAvatarUrl(reactionSender.Id);
        var tag = $"chat-{channel.Id}";
        var actions = new object[] { new { action = "open", title = "Open Chat" } };
        var serverName = _companyProfile.Get()?.CompanyName ?? "Spokes";
        var isGroupChat = channel.ChannelType != ChatChannelType.Direct;
        var channelName = isGroupChat ? channel.Name : $"DM with {reactionSender.FullName}";

        // 1. Dispatch In-App Notification (Rich Snackbar + Audio) to connected circuits
        _inAppDispatcher.Dispatch(new InAppNotification
        {
            RecipientUserId = originalSender.Id,
            Title = title,
            Content = truncatedMessage,
            SenderName = reactionSender.FullName,
            SenderAvatarUrl = icon,
            Initials = GetInitials(reactionSender.FullName),
            ChannelId = channel.Id,
            ChannelName = channelName ?? "Chat",
            ServerName = serverName,
            IsReaction = true,
            IsGroupChat = isGroupChat,
            Category = NotificationCategories.Chat,
            IsSilent = isSilent,
            IsEncrypted = message.IsEncrypted,
            MaskedContent = maskedBody
        });

        // 2. Dispatch Push Notification only if user is away/offline and has push enabled
        var tier = _pushDecision?.DetermineNotificationTier(originalSender.Id) ?? PresenceTier.All;
        if (tier == PresenceTier.None) return;

        if (!originalSender.PushNotificationsEnabled) return;

        var badgeCount = GetTotalBadgeCount(originalSender.Id);

        var request = new PushNotificationRequest
        {
            UserId = originalSender.Id,
            Title = title,
            Body = body,
            Url = url,
            Icon = icon,
            Tier = tier,
            Tag = tag,
            Actions = actions,
            Category = "chat",
            ThreadId = channel.Id,
            ServerName = serverName,
            ChannelName = channelName,
            IsGroupChat = isGroupChat,
            Badge = badgeCount,
            IsSilent = isSilent,
            IsEncrypted = message.IsEncrypted,
            MaskedBody = maskedBody
        };

        await _webPush.SendNotificationAsync(request);

        if (tier == PresenceTier.DesktopOnly)
        {
            _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification(request, message.Id, channel.Id, "Chat", TimeSpan.FromMinutes(1)));
        }
    }

    /// <summary>
    /// Evaluates email notification preferences and routes via Immediate Push or Delayed Mobile Queue.
    /// </summary>
    public async Task RouteEmailNotificationAsync(EmailMessage emailObj)
    {
        var employee = _employees.GetById(emailObj.EmployeeId);
        if (employee == null || !employee.PushNotificationsEnabled || !employee.EmailNotificationsEnabled) return;

        var tier = _pushDecision?.DetermineNotificationTier(emailObj.EmployeeId) ?? PresenceTier.All;
        var title = $"New Email from {emailObj.FromName}";
        var body = emailObj.Subject;

        var folder = _emailFolders.GetAll().FirstOrDefault(f => f.EmployeeId == emailObj.EmployeeId && f.Path == emailObj.FolderPath);
        var url = folder != null ? $"/email/{folder.Id}/{emailObj.Id}" : "/email";

        var icon = _pushTokenService?.GetDefaultIconUrl(emailObj.EmployeeId) ?? "/spokesapi/Media/Icon";
        var tag = $"email-{emailObj.EmployeeId}";
        var actions = new object[] { new { action = "open", title = "Read Email" } };

        if (tier == PresenceTier.None)
        {
            return;
        }

        var badgeCount = GetTotalBadgeCount(emailObj.EmployeeId);
        var request = new PushNotificationRequest
        {
            UserId = emailObj.EmployeeId,
            Title = title,
            Body = body,
            Url = url,
            Icon = icon,
            Tier = tier,
            Tag = tag,
            Actions = actions,
            Category = "email",
            Badge = badgeCount
        };

        await _webPush.SendNotificationAsync(request);

        if (tier == PresenceTier.DesktopOnly)
        {
            // Queue a Delayed Mobile Push
            _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification(request, emailObj.Id, "email", "Email", TimeSpan.FromMinutes(1)));
        }
    }

    /// <summary>
    /// Routes moderation notifications to all admins and moderators.
    /// </summary>
    public async Task RouteModerationNotificationAsync(ReportedMessage report)
    {
        var moderators = _employees.GetAll().Where(e => e.IsActive && e.HasPermission(Spokes_Server.Core.Constants.AppPermissions.Chat.Moderator));

        foreach (var mod in moderators)
        {
            if (!mod.PushNotificationsEnabled || !mod.ModerationNotificationsEnabled) continue;

            var tier = _pushDecision?.DetermineNotificationTier(mod.Id) ?? PresenceTier.All;
            if (tier == PresenceTier.None) continue;

            var title = "New Moderation Report";
            var body = $"A message was reported for {report.Reason}";
            var url = "/admin/moderation";
            var icon = GenerateAvatarUrl(mod.Id);
            var tag = $"mod-{report.Id}";
            var actions = new object[] { new { action = "open", title = "Review" } };

            var badgeCount = GetTotalBadgeCount(mod.Id);
            var request = new PushNotificationRequest
            {
                UserId = mod.Id,
                Title = title,
                Body = body,
                Url = url,
                Icon = icon,
                Tier = tier,
                Tag = tag,
                Actions = actions,
                Category = "moderation",
                Badge = badgeCount
            };

            await _webPush.SendNotificationAsync(request);

            if (tier == PresenceTier.DesktopOnly)
            {
                _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification(request, report.Id, "moderation", "Moderation", TimeSpan.FromMinutes(1)));
            }
        }
    }

    private string GenerateAvatarUrl(string senderId)
    {
        return _pushTokenService?.GetAvatarUrl(senderId) ?? $"/spokesapi/Media/Avatar/{senderId}";
    }

    private static string GetInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();
        }
        return name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant();
    }
}
