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
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Communication.Notifications.InApp;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

/// <summary>
/// Centralized routing engine for chat, email, and moderation notifications.
/// Evaluates channel access, user notification preferences, and presence tiers
/// to dispatch immediate pushes, delayed mobile pushes, and local snackbars.
/// </summary>
public class NotificationRoutingService : INotificationRoutingService
{
    private readonly EmployeeRepository _employees;
    private readonly ChatReadStateRepository _readStates;
    private readonly IWebPushService _webPush;
    private readonly NotificationQueueService _queue;
    private readonly GlobalKeystoreService _keystore;
    private readonly ICryptoService _crypto;
    private readonly PushTokenService? _pushTokenService;
    private readonly EmailFolderRepository _emailFolders;
    private readonly IEmailInboxService _emailInboxService;
    private readonly ChatChannelRepository _chatChannels;
    private readonly ChatMessageRepository _chatMessages;
    private readonly ReportedMessageRepository? _reportedMessages;
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
        IInAppNotificationDispatcher? inAppDispatcher = null,
        IEmailInboxService? emailInboxService = null)
    {
        ArgumentNullException.ThrowIfNull(communicationContext);
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(coreContext);

        _employees = hrContext.Employees;
        _readStates = communicationContext.ChatReadStates;
        _webPush = webPush;
        _queue = queue;
        _keystore = keystore;
        _crypto = crypto;
        _pushTokenService = pushTokenService ?? (dataProtection != null ? new PushTokenService(dataProtection) : null);
        _companyProfile = coreContext.CompanyProfile;
        _emailFolders = communicationContext.EmailFolders;
        _emailInboxService = emailInboxService ?? new EmailInboxService(
            communicationContext.EmailAccounts,
            communicationContext.EmailInboxes,
            communicationContext.EmailSendingIdentities,
            communicationContext.EmailFolders,
            communicationContext.EmailMessages,
            hrContext.Employees,
            hrContext.Teams,
            companyProfile: _companyProfile);
        _chatChannels = communicationContext.ChatChannels;
        _chatMessages = communicationContext.ChatMessages;
        _reportedMessages = communicationContext.ReportedMessages;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationRoutingService>.Instance;
        _chatAccess = chatAccess;
        _recipientFilter = recipientFilter ?? new NotificationRecipientFilter(_readStates, _chatMessages);
        _contentDecryptor = contentDecryptor ?? new NotificationContentDecryptor(_keystore, _crypto);
        _rateLimiter = rateLimiter ?? new NotificationRateLimiter(_readStates, _chatMessages);
        _pushDecision = pushDecision;
        _inAppDispatcher = inAppDispatcher ?? new InAppNotificationDispatcher();
    }

    private bool CanUserReceiveEmailBadges(Employee? user) =>
        user != null
        && FileAccessPermissionHelper.IsReadableUser(user)
        && !string.Equals(_companyProfile.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase)
        && FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Email.Use);

    public virtual int GetTotalBadgeCount(string userId) =>
        ComputeBadgeSummary(userId, includeDetails: false).Count;

    public virtual BadgeBreakdownResult GetBadgeBreakdown(string userId) =>
        ComputeBadgeSummary(userId, includeDetails: true);

    private BadgeBreakdownResult ComputeBadgeSummary(string userId, bool includeDetails)
    {
        int total = 0;
        int emailUnseenTotal = 0;
        var breakdown = includeDetails ? new Dictionary<string, int>() : null;
        var unreadByThread = includeDetails ? new Dictionary<string, int>() : null;
        var readWatermarksUtc = includeDetails ? new Dictionary<string, DateTime>() : null;
        var employee = _employees.GetById(userId);
        var lastEmailVisitedAt = employee?.LastEmailVisitedAt;

        // 1. Email Unseen Unread (since the user last visited the Email page)
        var emailThreadId = $"email-{userId}";
        if (CanUserReceiveEmailBadges(employee))
        {
            if (includeDetails && lastEmailVisitedAt.HasValue)
            {
                readWatermarksUtc![emailThreadId] = lastEmailVisitedAt.Value.UtcDateTime;
            }

            var unseenCount = _emailInboxService.GetUnseenUnreadCountForUser(userId);
            if (unseenCount > 0)
            {
                total += unseenCount;
                if (includeDetails)
                {
                    emailUnseenTotal += unseenCount;
                    breakdown!["Email_Unseen"] = unseenCount;
                }
            }

            if (includeDetails && emailUnseenTotal > 0)
            {
                unreadByThread![emailThreadId] = emailUnseenTotal;
            }
        }

        // 2. Chat Notified Unread
        var channels = _chatAccess.GetChannelsForUser(userId).Where(c => !c.IsArchived).ToList();
        foreach (var channel in channels)
        {
            var notifLevel = _readStates.GetNotificationLevel(userId, channel.Id, channel.ChannelType.ToString(), channel.IsVoiceChannel);
            if (notifLevel == ChatNotificationLevel.None) continue;

            var lastReadAt = _readStates.GetLastReadAt(userId, channel.Id);
            if (includeDetails && lastReadAt > DateTime.MinValue)
            {
                readWatermarksUtc![channel.Id] = lastReadAt.Kind == DateTimeKind.Utc
                    ? lastReadAt
                    : (lastReadAt.Kind == DateTimeKind.Local ? lastReadAt.ToUniversalTime() : DateTime.SpecifyKind(lastReadAt, DateTimeKind.Utc));
            }

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
                if (includeDetails)
                {
                    unreadByThread![channel.Id] = channelTotal;
                    var channelName = !string.IsNullOrEmpty(channel.Name) ? channel.Name : channel.Id;
                    breakdown![$"Chat_{channelName}"] = channelTotal;
                }
            }
        }

        // 3. Open Moderation Reports (populated in UnreadByThread for wake push reconciliation without altering email+chat badge total)
        if (includeDetails &&
            _reportedMessages != null &&
            ModerationAuthorizationHelper.CanModerateAny(employee) &&
            employee!.ModerationNotificationsEnabled)
        {
            foreach (var report in _reportedMessages.GetAll())
            {
                var channel = ModerationAuthorizationHelper.ResolveReportChannel(report, _chatChannels, _chatMessages);
                if (string.Equals(report.Status, ReportedMessageStatus.Open, StringComparison.OrdinalIgnoreCase) &&
                    ModerationAuthorizationHelper.CanViewReport(employee, report, channel, _chatAccess))
                {
                    unreadByThread![$"mod-{report.Id}"] = 1;
                }
            }
        }

        return new BadgeBreakdownResult
        {
            Count = total,
            Breakdown = breakdown ?? [],
            UnreadByThread = unreadByThread ?? [],
            ReadWatermarksUtc = readWatermarksUtc ?? [],
            ServerTimeUtc = DateTime.UtcNow
        };
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
            bool isCall = message.MessageType == ChatMessageType.CallInvite;
            string category = isCall ? NotificationCategories.Call : NotificationCategories.Chat;
            var formattedChannelName = channel.ChannelType == ChatChannelType.Direct && channel.ParticipantIds.Count == 2
                ? $"DM with {sender.FullName}"
                : (channel.Name ?? "Unknown Channel");

            await DispatchChatOrReactionNotificationAsync(
                recipient: employee!,
                actor: sender,
                message: message,
                channel: channel,
                inAppTitle: sender.FullName,
                pushTitle: title,
                inAppContent: body,
                pushBody: body,
                inAppChannelName: formattedChannelName,
                pushChannelName: channelName,
                serverName: serverName,
                isGroupChat: isGroupChat,
                isReaction: false,
                isCall: isCall,
                category: category,
                isSilent: isSilent,
                maskedBody: maskedBody,
                sentAtUtc: message.SentAt == default ? DateTime.UtcNow : message.SentAt);
        });

        await Task.WhenAll(tasks);
    }

    private async Task DispatchChatOrReactionNotificationAsync(
        Employee recipient,
        Employee actor,
        ChatMessage message,
        ChatChannel channel,
        string inAppTitle,
        string pushTitle,
        string inAppContent,
        string pushBody,
        string inAppChannelName,
        string? pushChannelName,
        string serverName,
        bool isGroupChat,
        bool isReaction,
        bool isCall,
        string category,
        bool isSilent,
        string? maskedBody,
        DateTime sentAtUtc)
    {
        var url = $"/chat/{channel.Id}";
        var icon = GenerateAvatarUrl(actor.Id);
        var tag = $"chat-{channel.Id}";

        // 1. Dispatch In-App Notification (Rich Snackbar + Audio) to connected circuits
        _inAppDispatcher.Dispatch(new InAppNotification
        {
            RecipientUserId = recipient.Id,
            Title = inAppTitle,
            Content = inAppContent,
            SenderName = actor.FullName,
            SenderAvatarUrl = icon,
            Initials = GetInitials(actor.FullName),
            ChannelId = channel.Id,
            ChannelName = inAppChannelName,
            ServerName = serverName,
            IsReaction = isReaction,
            IsGroupChat = isGroupChat,
            Category = category,
            IsSilent = isSilent,
            IsEncrypted = message.IsEncrypted,
            MaskedContent = maskedBody
        });

        // 2. Dispatch Push Notification only if user is away/offline and has push enabled
        var tier = _pushDecision?.DetermineNotificationTier(recipient.Id) ?? PresenceTier.All;
        if (!ApplyChatPushRouting(recipient.Id, isCall, ref tier, out var excludedSessionIds))
        {
            return;
        }

        if (!recipient.PushNotificationsEnabled)
        {
            return;
        }

        var userSoundChoice = recipient.GetNotificationSound(category);
        var sound = NotificationSoundCatalog.GetEffectivePushSound(category, userSoundChoice);

        var request = new PushNotificationRequest
        {
            UserId = recipient.Id,
            Title = pushTitle,
            Body = pushBody,
            Url = url,
            Icon = icon,
            Tier = tier,
            Tag = tag,
            Actions = [new { action = "open", title = "Open Chat" }],
            Category = category,
            ThreadId = channel.Id,
            ServerName = serverName,
            ChannelName = pushChannelName,
            IsGroupChat = isGroupChat,
            Badge = GetTotalBadgeCount(recipient.Id),
            IsSilent = isSilent,
            Sound = sound,
            ExcludedSessionIds = excludedSessionIds,
            SentAtUtc = sentAtUtc,
            IsEncrypted = message.IsEncrypted,
            MaskedBody = maskedBody,
            SourceChannelId = channel.Id,
            SourceMessageId = message.Id
        };

        await DispatchPushWithDelayedMobileAsync(
            request,
            tier,
            () => new DelayedMobileNotification(request, message.Id, channel.Id, "Chat", TimeSpan.FromMinutes(1)));
    }

    private async Task DispatchPushWithDelayedMobileAsync(
        PushNotificationRequest request,
        PresenceTier tier,
        Func<DelayedMobileNotification> delayedFactory)
    {
        await _webPush.SendNotificationAsync(request);

        if (tier == PresenceTier.DesktopOnly)
        {
            _queue.EnqueueDelayedMobilePush(delayedFactory());
        }
    }

    private bool ApplyChatPushRouting(
        string userId,
        bool isCall,
        ref PresenceTier tier,
        out IReadOnlyCollection<string>? excludedSessionIds)
    {
        excludedSessionIds = null;
        if (isCall)
        {
            if (tier == PresenceTier.DesktopOnly)
            {
                // Incoming voice calls are time-critical: notify mobile devices immediately instead of delaying 60s
                tier = PresenceTier.All;
                return true;
            }

            if (tier == PresenceTier.None)
            {
                var focusedSessions = _pushDecision?.GetFocusedSessionIds(userId);
                if (focusedSessions is { Count: > 0 })
                {
                    // Exclude only the actively focused device session(s) (which already play the in-app ringtone)
                    // so any other connected phones/devices still ring via push.
                    tier = PresenceTier.All;
                    excludedSessionIds = focusedSessions;
                    return true;
                }

                return false;
            }

            return true;
        }

        return tier != PresenceTier.None;
    }

    public async Task ClearChannelNotificationsAsync(string channelId)
    {
        var participantIds = _chatAccess.GetUsersForChannel(channelId);
        foreach (var userId in participantIds)
        {
            try
            {
                await _webPush.SendClearNotificationAsync(userId, $"call_{channelId}");
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

        bool isSilent = _rateLimiter.ShouldSuppressSound(originalSender!, channel.Id);
        var truncatedMessage = _contentDecryptor.DecryptAndFormatContent(message, channel, originalSender!.Id, 40, isReaction: true);
        var maskedBody = message.IsEncrypted ? "🔒 Secure Message" : null;
        var title = $"{reactionSender.FirstName} reacted {emoji} to:";
        var serverName = _companyProfile.Get()?.CompanyName ?? "Spokes";
        var isGroupChat = channel.ChannelType != ChatChannelType.Direct;
        var channelName = isGroupChat ? channel.Name : $"DM with {reactionSender.FullName}";

        await DispatchChatOrReactionNotificationAsync(
            recipient: originalSender,
            actor: reactionSender,
            message: message,
            channel: channel,
            inAppTitle: title,
            pushTitle: title,
            inAppContent: truncatedMessage,
            pushBody: truncatedMessage,
            inAppChannelName: channelName ?? "Chat",
            pushChannelName: channelName,
            serverName: serverName,
            isGroupChat: isGroupChat,
            isReaction: true,
            isCall: false,
            category: NotificationCategories.Chat,
            isSilent: isSilent,
            maskedBody: maskedBody,
            sentAtUtc: DateTime.UtcNow);
    }

    /// <summary>
    /// Evaluates email notification preferences and routes via Immediate Push or Delayed Mobile Queue.
    /// </summary>
    public async Task RouteEmailNotificationAsync(EmailMessage emailObj)
    {
        if (string.Equals(_companyProfile.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
            return;

        var recipientIds = _emailInboxService.GetAuthorizedEmployeeIdsForMessage(emailObj);
        if (recipientIds.Count == 0) return;

        var folder = _emailFolders.GetByAccount(emailObj.AccountId).FirstOrDefault(f => f.Path == emailObj.FolderPath);
        var url = folder != null ? $"/email/{folder.Id}/{emailObj.Id}" : "/email";
        var title = $"New Email from {(!string.IsNullOrWhiteSpace(emailObj.FromName) ? emailObj.FromName : emailObj.FromAddress)}";
        var body = emailObj.Subject;
        var actions = new object[] { new { action = "open", title = "Read Email" } };

        foreach (var userId in recipientIds)
        {
            var employee = _employees.GetById(userId);
            if (employee == null
                || !employee.IsActive
                || employee.IsBanned
                || employee.IsSuspended
                || !FileAccessPermissionHelper.HasAssignedPermission(employee, AppPermissions.Email.Use)
                || !employee.PushNotificationsEnabled
                || !employee.EmailNotificationsEnabled) continue;

            var tier = _pushDecision?.DetermineNotificationTier(userId) ?? PresenceTier.All;
            if (tier == PresenceTier.None) continue;

            var icon = _pushTokenService?.GetDefaultIconUrl(userId) ?? "/spokesapi/Media/Icon";
            var tag = $"email-{userId}";
            var badgeCount = GetTotalBadgeCount(userId);

            var userSoundChoice = employee.GetNotificationSound(NotificationCategories.Email);
            var sound = NotificationSoundCatalog.GetEffectivePushSound(NotificationCategories.Email, userSoundChoice);

            var request = new PushNotificationRequest
            {
                UserId = userId,
                Title = title,
                Body = body,
                Url = url,
                Icon = icon,
                Tier = tier,
                Tag = tag,
                ThreadId = tag,
                Actions = actions,
                Category = NotificationCategories.Email,
                Sound = sound,
                Badge = badgeCount,
                SentAtUtc = emailObj.Date == default ? DateTime.UtcNow : emailObj.Date.UtcDateTime,
                SourceEmailAccountId = emailObj.AccountId,
                SourceEmailFolderPath = emailObj.FolderPath,
                SourceEmailMessageId = emailObj.Id
            };

            await DispatchPushWithDelayedMobileAsync(
                request,
                tier,
                () =>
                {
                    var contextChannelId = !string.IsNullOrEmpty(emailObj.AccountId) ? emailObj.AccountId : "email";
                    return new DelayedMobileNotification(
                        request,
                        emailObj.Id,
                        contextChannelId,
                        "Email",
                        TimeSpan.FromMinutes(1),
                        accountId: emailObj.AccountId,
                        folderPath: emailObj.FolderPath);
                });
        }
    }

    /// <summary>
    /// Routes moderation notifications to all admins and moderators.
    /// </summary>
    public async Task RouteModerationNotificationAsync(ReportedMessage report)
    {
        var channel = ModerationAuthorizationHelper.ResolveReportChannel(report, _chatChannels, _chatMessages);
        var moderators = _employees.GetAll().Where(e => ModerationAuthorizationHelper.CanViewReport(e, report, channel, _chatAccess));

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
                ThreadId = tag,
                Actions = actions,
                Category = "moderation",
                Badge = badgeCount,
                SentAtUtc = report.ReportedAt == default ? DateTime.UtcNow : report.ReportedAt,
                SourceReportId = report.Id,
                SourceChannelId = channel?.Id ?? report.ChannelId,
                SourceMessageId = report.MessageId
            };

            await DispatchPushWithDelayedMobileAsync(
                request,
                tier,
                () => new DelayedMobileNotification(request, report.Id, "moderation", "Moderation", TimeSpan.FromMinutes(1)));
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
