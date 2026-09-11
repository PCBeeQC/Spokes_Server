using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Communication.Notifications;

/// <summary>
/// Centralized routing engine for chat notifications.
/// Evaluates channel access, user notification preferences, and presence tiers
/// to dispatch immediate pushes, delayed mobile pushes, and local snackbars.
/// </summary>
public class NotificationRoutingService
{
    private readonly EmployeeRepository _employees;
    private readonly ProjectRepository _projects;
    private readonly ChatReadStateRepository _readStates;
    private readonly TeamRepository _teams;
    private readonly IWebPushService _webPush;
    private readonly NotificationQueueService _queue;
    private readonly GlobalKeystoreService _keystore;
    private readonly ICryptoService _crypto;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly ChatChannelRepository _chatChannels;
    private readonly ChatMessageRepository _chatMessages;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly ILogger<NotificationRoutingService> _logger;
    private readonly System.IServiceProvider _serviceProvider;
    private Spokes_Server.Core.Services.Communication.Chat.IChatChannelAccessService? _chatAccess;
    private Spokes_Server.Core.Services.Communication.Chat.IChatChannelAccessService ChatAccess => _chatAccess ??= Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Spokes_Server.Core.Services.Communication.Chat.IChatChannelAccessService>(_serviceProvider);

    public NotificationRoutingService(
        EmployeeRepository employees,
        ProjectRepository projects,
        ChatReadStateRepository readStates,
        TeamRepository teams,
        IWebPushService webPush,
        NotificationQueueService queue,
        GlobalKeystoreService keystore,
        ICryptoService crypto,
        IDataProtectionProvider dataProtection,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        ChatChannelRepository chatChannels,
        ChatMessageRepository chatMessages,
        CompanyProfileRepository companyProfile,
        ILogger<NotificationRoutingService> logger,
        System.IServiceProvider serviceProvider)
    {
        _employees = employees;
        _projects = projects;
        _readStates = readStates;
        _teams = teams;
        _webPush = webPush;
        _queue = queue;
        _keystore = keystore;
        _crypto = crypto;
        _dataProtection = dataProtection;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _chatChannels = chatChannels;
        _chatMessages = chatMessages;
        _companyProfile = companyProfile;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task<int> GetTotalBadgeCountAsync(string userId)
    {
        int total = 0;

        // 1. Email Unread
        var folders = _emailFolders.GetByEmployee(userId);
        foreach (var folder in folders)
        {
            if (folder.IsTrash || folder.IsDrafts) continue;
            total += _emailMessages.GetUnreadCount(userId, folder.Path);
        }

        // 2. Chat Notified Unread
        var channels = ChatAccess.GetChannelsForUser(userId).Where(c => !c.IsArchived).ToList();
        var employee = _employees.GetById(userId);
        foreach (var channel in channels)
        {
            var notifLevel = _readStates.GetNotificationLevel(userId, channel.Id, channel.ChannelType.ToString(), channel.IsVoiceChannel);
            if (notifLevel == "None") continue;

            var lastReadAt = _readStates.GetLastReadAt(userId, channel.Id);
            var unreadMessages = await _chatMessages.GetUnreadMessagesSinceAsync(channel.Id, lastReadAt, userId);

            if (notifLevel == "All")
            {
                total += unreadMessages.Count;
            }
            else if (notifLevel == "Mentions")
            {
                var channelIndex = _chatMessages.GetIndexForChannel(channel.Id);
                foreach (var msg in unreadMessages)
                {
                    bool isMentioned = msg.MentionedUserIds.Contains(userId) || msg.MentionsEveryone;
                    if (!isMentioned && msg.MentionsTeam)
                    {
                        var isOnTeam = !string.IsNullOrEmpty(employee?.TeamId) || _teams.GetAll().Any(t => t.LeaderId == userId);
                        if (isOnTeam) isMentioned = true;
                    }
                    if (!isMentioned && employee?.ReplyNotificationsTreatAsMention == true && !string.IsNullOrEmpty(msg.ReplyToId))
                    {
                        var parentEntry = channelIndex.FirstOrDefault(e => e.Id == msg.ReplyToId);
                        if (parentEntry != null && parentEntry.SenderId == userId)
                        {
                            isMentioned = true;
                        }
                    }
                    if (isMentioned) total++;
                }
            }
        }

        return total;
    }

    public async Task<object> GetBadgeBreakdownAsync(string userId)
    {
        int total = 0;
        var breakdown = new Dictionary<string, int>();

        // 1. Email Unread
        var folders = _emailFolders.GetByEmployee(userId);
        foreach (var folder in folders)
        {
            if (folder.IsTrash || folder.IsDrafts) continue;
            var unreadCount = _emailMessages.GetUnreadCount(userId, folder.Path);
            if (unreadCount > 0)
            {
                total += unreadCount;
                breakdown[$"Email_{folder.Path}"] = unreadCount;
            }
        }

        // 2. Chat Notified Unread
        var channels = ChatAccess.GetChannelsForUser(userId).Where(c => !c.IsArchived).ToList();
        var employee = _employees.GetById(userId);
        foreach (var channel in channels)
        {
            var notifLevel = _readStates.GetNotificationLevel(userId, channel.Id, channel.ChannelType.ToString(), channel.IsVoiceChannel);
            if (notifLevel == "None") continue;

            var lastReadAt = _readStates.GetLastReadAt(userId, channel.Id);
            var unreadMessages = await _chatMessages.GetUnreadMessagesSinceAsync(channel.Id, lastReadAt, userId);

            int channelTotal = 0;
            if (notifLevel == "All")
            {
                channelTotal = unreadMessages.Count;
            }
            else if (notifLevel == "Mentions")
            {
                var channelIndex = _chatMessages.GetIndexForChannel(channel.Id);
                foreach (var msg in unreadMessages)
                {
                    bool isMentioned = msg.MentionedUserIds.Contains(userId) || msg.MentionsEveryone;
                    if (!isMentioned && msg.MentionsTeam)
                    {
                        var isOnTeam = !string.IsNullOrEmpty(employee?.TeamId) || _teams.GetAll().Any(t => t.LeaderId == userId);
                        if (isOnTeam) isMentioned = true;
                    }
                    if (!isMentioned && employee?.ReplyNotificationsTreatAsMention == true && !string.IsNullOrEmpty(msg.ReplyToId))
                    {
                        var parentEntry = channelIndex.FirstOrDefault(e => e.Id == msg.ReplyToId);
                        if (parentEntry != null && parentEntry.SenderId == userId)
                        {
                            isMentioned = true;
                        }
                    }
                    if (isMentioned) channelTotal++;
                }
            }

            if (channelTotal > 0)
            {
                total += channelTotal;
                var channelName = !string.IsNullOrEmpty(channel.Name) ? channel.Name : channel.Id;
                breakdown[$"Chat_{channelName}"] = channelTotal;
            }
        }

        return new { count = total, breakdown = breakdown };
    }

    /// <summary>
    /// Evaluates recipients and routes the notification via Immediate Push or Delayed Mobile Queue.
    /// Note: Local Blazor Server snackbars are triggered via ChatStateService independently on all connected clients.
    /// </summary>
    public async Task RouteChatNotificationAsync(ChatMessage message, ChatChannel channel, Employee sender)
    {
        var recipientIds = ChatAccess.GetUsersForChannel(channel.Id).Where(id => id != sender.Id).ToHashSet();
        var serverName = _companyProfile.Get()?.CompanyName ?? "Spokes";
        var isGroupChat = channel.ChannelType != ChatChannelType.Direct;
        var channelName = isGroupChat ? channel.Name : null;

        foreach (var userId in recipientIds)
        {
            var employee = _employees.GetById(userId);
            if (employee == null || !employee.ChatNotificationsEnabled) continue;

            // Skip if this employee has blocked the sender
            if (employee.BlockedUserIds?.Contains(sender.Id) == true) continue;

            // 1. Check user's channel notification preference
            var notifLevel = _readStates.GetNotificationLevel(userId, channel.Id, channel.ChannelType.ToString(), channel.IsVoiceChannel);
            if (notifLevel == "None") continue;

            if (notifLevel == "Mentions")
            {
                var isMentioned = message.MentionedUserIds.Contains(userId) || message.MentionsEveryone;
                if (!isMentioned && message.MentionsTeam)
                {
                    var isOnTeam = !string.IsNullOrEmpty(employee.TeamId) || _teams.GetAll().Any(t => t.LeaderId == userId);
                    if (isOnTeam) isMentioned = true;
                }
                
                if (!isMentioned && employee.ReplyNotificationsTreatAsMention && !string.IsNullOrEmpty(message.ReplyToId))
                {
                    var parentEntry = _chatMessages.GetIndexForChannel(channel.Id).FirstOrDefault(e => e.Id == message.ReplyToId);
                    if (parentEntry != null && parentEntry.SenderId == userId)
                    {
                        isMentioned = true;
                    }
                }
                
                if (!isMentioned) continue; // Skip if they requested Mentions Only and weren't mentioned
            }

            // 2. Evaluate Presence Tier and Route
            bool isSilent = false;
            if (employee.LimitConsecutiveNotificationSounds)
            {
                var lastReadAt = _readStates.GetLastReadAt(userId, channel.Id);
                var unreadMessages = await _chatMessages.GetUnreadMessagesSinceAsync(channel.Id, lastReadAt, userId);
                isSilent = unreadMessages.Count > employee.ConsecutiveNotificationSoundLimit;
            }

            var tier = _webPush.DetermineNotificationTier(userId);
            var title = $"{sender.FirstName} {sender.LastName}".Trim();

            var body = message.Content;

            if (message.IsEncrypted)
            {
                body = "🔒 New Secure Message"; // Fallback generic text

                if (_keystore.IsUserUnlocked(userId))
                {
                    var privateKey = _keystore.GetPrivateKey(userId);
                    if (privateKey != null && channel.EncryptedChannelKeys.TryGetValue(userId, out var cipherKey))
                    {
                        try
                        {
                            var channelKey = _crypto.DecryptRsa(cipherKey, privateKey);
                            body = _crypto.DecryptAes(message.Content, channelKey);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to decrypt message body for user {UserId} in notification routing", userId);
                        }
                    }
                }
            }

            body = Spokes_Server.Core.Helpers.ChatPreviewFormatter.FormatPreviewContent(body, message.Attachments);

            if (body.Length > 100) body = body.Substring(0, 97) + "...";
            var url = $"/chat/{channel.Id}";

            var protector = _dataProtection.CreateProtector("AvatarPushToken");
            var token = protector.Protect($"{sender.Id}|{System.DateTime.UtcNow.AddHours(48).Ticks}");
            var encodedToken = System.Net.WebUtility.UrlEncode(token);

            var icon = $"/spokesapi/Media/Avatar/{sender.Id}?t={encodedToken}";
            var tag = $"chat-{channel.Id}";
            var actions = new object[]
            {
                new { action = "open", title = "Open Chat" }
            };

            if (tier == PresenceTier.None)
            {
                continue; // Do nothing, local blazor handles snackbars immediately. No web push.
            }
            if (tier == PresenceTier.DesktopOnly)
            {
                var badgeCount = await GetTotalBadgeCountAsync(userId);
                // Send immediate Desktop Push
                await _webPush.SendNotificationAsync(userId, title, body, url, icon, PresenceTier.DesktopOnly, tag, actions, threadId: channel.Id, serverName: serverName, channelName: channelName, isGroupChat: isGroupChat, badge: badgeCount, isSilent: isSilent);

                // Queue a Delayed Mobile Push to fire in 1 minute if they don't read it
                _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification
                {
                    UserId = userId,
                    MessageId = message.Id,
                    ChannelId = channel.Id,
                    Title = title,
                    Body = body,
                    Url = url,
                    Icon = icon,
                    Tag = tag,
                    Actions = actions,
                    ProcessAtUtc = System.DateTime.UtcNow.AddMinutes(1),
                    ServerName = serverName,
                    ChannelName = channelName,
                    IsGroupChat = isGroupChat,
                    IsSilent = isSilent
                });
            }
            else
            {
                var badgeCount = await GetTotalBadgeCountAsync(userId);
                // Tier is 'All' (no desktop seen in >15m) -> Dispatch immediately to Desktop + Mobile
                await _webPush.SendNotificationAsync(userId, title, body, url, icon, PresenceTier.All, tag, actions, threadId: channel.Id, serverName: serverName, channelName: channelName, isGroupChat: isGroupChat, badge: badgeCount, isSilent: isSilent);
            }
        }
    }

    public async Task RouteReactionNotificationAsync(ChatMessage message, ChatChannel channel, string emoji, Employee reactionSender)
    {
        if (message.SenderId == reactionSender.Id) return;

        var serverName = _companyProfile.Get()?.CompanyName ?? "Spokes";
        var isGroupChat = channel.ChannelType != ChatChannelType.Direct;
        var channelName = isGroupChat ? channel.Name : null;

        var originalSender = _employees.GetById(message.SenderId);
        if (originalSender == null || !originalSender.PushNotificationsEnabled || !originalSender.ReactionNotificationsEnabled) return;

        // Skip if originalSender blocked reactionSender
        if (originalSender.BlockedUserIds?.Contains(reactionSender.Id) == true) return;

        var tier = _webPush.DetermineNotificationTier(originalSender.Id);
        if (tier == PresenceTier.None) return;

        bool isSilent = false;
        if (originalSender.LimitConsecutiveNotificationSounds)
        {
            var lastReadAt = _readStates.GetLastReadAt(originalSender.Id, channel.Id);
            var unreadMessages = await _chatMessages.GetUnreadMessagesSinceAsync(channel.Id, lastReadAt, originalSender.Id);
            isSilent = unreadMessages.Count > originalSender.ConsecutiveNotificationSoundLimit;
        }

        var truncatedMessage = message.Content;
        if (message.IsEncrypted)
        {
            truncatedMessage = "🔒 Secure Message";
            if (_keystore.IsUserUnlocked(originalSender.Id))
            {
                var privateKey = _keystore.GetPrivateKey(originalSender.Id);
                if (privateKey != null && channel.EncryptedChannelKeys.TryGetValue(originalSender.Id, out var cipherKey))
                {
                        try
                        {
                            var channelKey = _crypto.DecryptRsa(cipherKey, privateKey);
                            var decryptedContent = _crypto.DecryptAes(message.Content, channelKey);
                            truncatedMessage = Spokes_Server.Core.Helpers.ChatPreviewFormatter.FormatPreviewContent(decryptedContent, message.Attachments);
                            if (truncatedMessage.Length > 40) truncatedMessage = truncatedMessage.Substring(0, 37) + "...";
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to decrypt message body for user {UserId} in reaction notification routing", originalSender.Id);
                        }
                }
            }
        }
        else
        {
            truncatedMessage = Spokes_Server.Core.Helpers.ChatPreviewFormatter.FormatPreviewContent(truncatedMessage, message.Attachments);
            if (truncatedMessage.Length > 40) truncatedMessage = truncatedMessage.Substring(0, 37) + "...";
        }

        var title = $"{reactionSender.FirstName} reacted {emoji} to:";
        var body = $"{truncatedMessage}";

        var url = $"/chat/{channel.Id}";

        var protector = _dataProtection.CreateProtector("AvatarPushToken");
        var token = protector.Protect($"{reactionSender.Id}|{System.DateTime.UtcNow.AddHours(48).Ticks}");
        var encodedToken = System.Net.WebUtility.UrlEncode(token);

        var icon = $"/spokesapi/Media/Avatar/{reactionSender.Id}?t={encodedToken}";
        var tag = $"chat-{channel.Id}";
        var actions = new object[] { new { action = "open", title = "Open Chat" } };

        if (tier == PresenceTier.DesktopOnly)
        {
            var badgeCount = await GetTotalBadgeCountAsync(originalSender.Id);
            await _webPush.SendNotificationAsync(originalSender.Id, title, body, url, icon, PresenceTier.DesktopOnly, tag, actions, threadId: channel.Id, serverName: serverName, channelName: channelName, isGroupChat: isGroupChat, badge: badgeCount, isSilent: isSilent);

            _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification
            {
                UserId = originalSender.Id,
                MessageId = message.Id,
                ChannelId = channel.Id,
                Title = title,
                Body = body,
                Url = url,
                Icon = icon,
                Tag = tag,
                Actions = actions,
                ProcessAtUtc = System.DateTime.UtcNow.AddMinutes(1),
                ServerName = serverName,
                ChannelName = channelName,
                IsGroupChat = isGroupChat,
                IsSilent = isSilent
            });
        }
        else
        {
            var badgeCount = await GetTotalBadgeCountAsync(originalSender.Id);
            await _webPush.SendNotificationAsync(originalSender.Id, title, body, url, icon, PresenceTier.All, tag, actions, threadId: channel.Id, serverName: serverName, channelName: channelName, isGroupChat: isGroupChat, badge: badgeCount, isSilent: isSilent);
        }
    }



    /// <summary>
    /// Evaluates email notification preferences and routes via Immediate Push or Delayed Mobile Queue.
    /// </summary>
    public async Task RouteEmailNotificationAsync(EmailMessage emailObj)
    {
        var employee = _employees.GetById(emailObj.EmployeeId);
        if (employee == null || !employee.PushNotificationsEnabled || !employee.EmailNotificationsEnabled) return;

        var tier = _webPush.DetermineNotificationTier(emailObj.EmployeeId);
        var title = $"New Email from {emailObj.FromName}";
        var body = emailObj.Subject;

        var folder = _emailFolders.GetAll().FirstOrDefault(f => f.EmployeeId == emailObj.EmployeeId && f.Path == emailObj.FolderPath);
        var url = folder != null ? $"/email/{folder.Id}/{emailObj.Id}" : "/email";

        var protector = _dataProtection.CreateProtector("AvatarPushToken");
        var token = protector.Protect($"{emailObj.EmployeeId}|{System.DateTime.UtcNow.AddHours(48).Ticks}");
        var encodedToken = System.Net.WebUtility.UrlEncode(token);
        var icon = $"/spokesapi/Media/Icon?t={encodedToken}";
        var tag = $"email-{emailObj.EmployeeId}";
        var actions = new object[] { new { action = "open", title = "Read Email" } };

        if (tier == PresenceTier.None)
        {
            return;
        }
        else if (tier == PresenceTier.DesktopOnly)
        {
            var badgeCount = await GetTotalBadgeCountAsync(emailObj.EmployeeId);
            // Send immediate Desktop Push
            await _webPush.SendNotificationAsync(emailObj.EmployeeId, title, body, url, icon, PresenceTier.DesktopOnly, tag, actions, badge: badgeCount);

            // Queue a Delayed Mobile Push
            _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification
            {
                Type = "Email",
                UserId = emailObj.EmployeeId,
                MessageId = emailObj.Id,
                Title = title,
                Body = body,
                Url = url,
                Icon = icon,
                Tag = tag,
                Actions = actions,
                ProcessAtUtc = System.DateTime.UtcNow.AddMinutes(1)
            });
        }
        else
        {
            var badgeCount = await GetTotalBadgeCountAsync(emailObj.EmployeeId);
            await _webPush.SendNotificationAsync(emailObj.EmployeeId, title, body, url, icon, PresenceTier.All, tag, actions, badge: badgeCount);
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

            var tier = _webPush.DetermineNotificationTier(mod.Id);
            if (tier == PresenceTier.None) continue;

            var title = "New Moderation Report";
            var body = $"A message was reported for {report.Reason}";
            var url = "/admin/moderation";

            var protector = _dataProtection.CreateProtector("AvatarPushToken");
            var token = protector.Protect($"{mod.Id}|{System.DateTime.UtcNow.AddHours(48).Ticks}");
            var encodedToken = System.Net.WebUtility.UrlEncode(token);
            var icon = $"/spokesapi/Media/Avatar/{mod.Id}?t={encodedToken}";
            var tag = $"mod-{report.Id}";
            var actions = new object[] { new { action = "open", title = "Review" } };

            if (tier == PresenceTier.DesktopOnly)
            {
                var badgeCount = await GetTotalBadgeCountAsync(mod.Id);
                await _webPush.SendNotificationAsync(mod.Id, title, body, url, icon, PresenceTier.DesktopOnly, tag, actions, badge: badgeCount);

                _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification
                {
                    UserId = mod.Id,
                    MessageId = report.Id, // Reusing MessageId field for report Id
                    ChannelId = "moderation",
                    Title = title,
                    Body = body,
                    Url = url,
                    Icon = icon,
                    Tag = tag,
                    Actions = actions,
                    ProcessAtUtc = System.DateTime.UtcNow.AddMinutes(1)
                });
            }
            else
            {
                var badgeCount = await GetTotalBadgeCountAsync(mod.Id);
                await _webPush.SendNotificationAsync(mod.Id, title, body, url, icon, PresenceTier.All, tag, actions, badge: badgeCount);
            }
        }
    }
}

