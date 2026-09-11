using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.SignalR;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Hubs;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Aggregate;
using Microsoft.Extensions.Logging;
using Livekit.Server;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Core.Services.Communication.Chat;

public interface IChatChannelAccessService
{
    List<ChatChannel> GetChannelsForUser(string userId);
    List<string> GetUsersForChannel(string channelId);
}

public class ChatService : IChatChannelAccessService
{
    private readonly EmployeeRepository _employees;
    private readonly ChatChannelRepository _channels;
    private readonly ChatMessageRepository _messages;
    private readonly TeamRepository _teams;
    private readonly ChatReadStateRepository _readStates;
    private readonly ProjectRepository _projects;

    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ChatStateService _chatState;
    private readonly NotificationRoutingService _notificationRouting;
    private readonly PresenceStateService _presenceState;
    private readonly ILogger<ChatService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IFileService _fileService;
    private readonly ICryptoService _crypto;
    private readonly GlobalKeystoreService _keystore;
    private readonly ServerEscrowService _escrowService;
    private readonly IContentModerationService _moderationService;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly AlbumService _albumService;
    private readonly MarkdownSanitizerService _markdownSanitizer;
    private readonly CalendarEventRepository? _calendarEvents;

    public ChatService(
        EmployeeRepository employees,
        ChatChannelRepository channels,
        ChatMessageRepository messages,
        TeamRepository teams,
        ChatReadStateRepository readStates,
        ProjectRepository projects,
        IHubContext<ChatHub> hubContext,
        ChatStateService chatState,
        NotificationRoutingService notificationRouting,
        PresenceStateService presenceState,
        ILogger<ChatService> logger,
        IConfiguration configuration,
        IFileService fileService,
        ICryptoService crypto,
        GlobalKeystoreService keystore,
        ServerEscrowService escrowService,
        IContentModerationService moderationService,
        SystemConfigRepository systemConfigs,
        CompanyProfileRepository companyProfiles,
        AlbumService albumService,
        MarkdownSanitizerService markdownSanitizer,
        CalendarEventRepository? calendarEvents = null)
    {
        _employees = employees;
        _channels = channels;
        _messages = messages;
        _teams = teams;
        _readStates = readStates;
        _projects = projects;
        _hubContext = hubContext;
        _chatState = chatState;
        _notificationRouting = notificationRouting;
        _presenceState = presenceState;
        _logger = logger;
        _configuration = configuration;
        _fileService = fileService;
        _crypto = crypto;
        _keystore = keystore;
        _escrowService = escrowService;
        _moderationService = moderationService;
        _systemConfigs = systemConfigs;
        _companyProfiles = companyProfiles;
        _albumService = albumService;
        _markdownSanitizer = markdownSanitizer;
        _calendarEvents = calendarEvents;
    }

    public async Task<bool> RsvpToEventAsync(string eventId, string userId)
    {
        if (_calendarEvents == null || string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(userId))
            return false;

        var evt = _calendarEvents.GetById(eventId);
        if (evt == null)
            return false;

        evt.Attendees ??= new List<string>();
        if (!evt.Attendees.Contains(userId))
        {
            evt.Attendees.Add(userId);
            _calendarEvents.Save(evt);
            _chatState.NotifyCalendarEventUpdated(evt);
            return true;
        }

        return false;
    }

    public async Task<bool> CancelRsvpEventAsync(string eventId, string userId)
    {
        if (_calendarEvents == null || string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(userId))
            return false;

        var evt = _calendarEvents.GetById(eventId);
        if (evt == null || evt.Attendees == null)
            return false;

        if (evt.Attendees.Contains(userId))
        {
            evt.Attendees.Remove(userId);
            _calendarEvents.Save(evt);
            _chatState.NotifyCalendarEventUpdated(evt);
            return true;
        }

        return false;
    }

    public bool CanUserPostToChannel(ChatChannel channel, Employee employee)
    {
        if (!channel.IsAnnouncementOnly) return true;

        var userTeamIds = new System.Collections.Generic.List<string>();
        if (employee.TeamId != null) userTeamIds.Add(employee.TeamId);
        userTeamIds.AddRange(_teams.GetAll().Where(t => t.LeaderId == employee.Id).Select(t => t.Id));

        return _channels.EvaluateChannelPostAccessRule(channel, employee.Id, userTeamIds, employee.IsAdmin);
    }

    public virtual List<ChatChannel> GetChannelsForUser(string userId)
    {
        var employee = _employees.GetById(userId);
        if (employee == null) return new List<ChatChannel>();

        var allProjects = _projects.GetAll();
        var userProjectIds = new List<string>();
        bool isAdmin = employee.IsAdmin;

        foreach (var p in allProjects)
        {
            if (isAdmin || p.AccessPolicy == "Public")
            {
                userProjectIds.Add(p.Id);
            }
            else
            {
                bool isAllowed = p.AllowedUserIds.Contains(userId);
                if (!isAllowed && employee.TeamId != null && p.AllowedTeamIds.Contains(employee.TeamId))
                {
                    isAllowed = true;
                }
                if (isAllowed) userProjectIds.Add(p.Id);
            }
        }

        var userTeamIds = new List<string>();
        if (employee.TeamId != null) userTeamIds.Add(employee.TeamId);
        userTeamIds.AddRange(_teams.GetAll().Where(t => t.LeaderId == userId).Select(t => t.Id));

        var channels = _channels.GetChannelsForUser(userId, userProjectIds, userTeamIds, isAdmin);
        
        if (!employee.HasPermission(Spokes_Server.Core.Constants.AppPermissions.Chat.ViewArchive))
        {
            channels = channels.Where(c => !c.IsArchived).ToList();
        }
        
        return channels;
    }

    public virtual List<string> GetUsersForChannel(string channelId)
    {
        var channel = _channels.GetById(channelId);
        if (channel == null) return new List<string>();

        var validUserIds = new List<string>();
        var allActiveEmployees = _employees.GetAll().Where(e => e.IsActive).ToList();
        
        // Pre-compute team leaders to avoid repeated lookups
        var teamLeaderIds = _teams.GetAll().Select(t => t.LeaderId).ToHashSet();
        
        Project? project = null;
        if (channel.ChannelType == ChatChannelType.Project && channel.LinkedEntityId != null)
        {
            project = _projects.GetById(channel.LinkedEntityId);
        }

        foreach (var emp in allActiveEmployees)
        {
            var userTeamIds = new List<string>();
            if (emp.TeamId != null) userTeamIds.Add(emp.TeamId);
            if (teamLeaderIds.Contains(emp.Id))
            {
                userTeamIds.AddRange(_teams.GetAll().Where(t => t.LeaderId == emp.Id).Select(t => t.Id));
            }
            
            // To evaluate Project channel logic identically to GetChannelsForUser, 
            // we must check if the user has access to the project.
            var userProjectIds = new List<string>();
            if (project != null)
            {
                if (emp.IsAdmin || project.AccessPolicy == "Public" ||
                    project.AllowedUserIds.Contains(emp.Id) ||
                    (emp.TeamId != null && project.AllowedTeamIds.Contains(emp.TeamId)))
                {
                    userProjectIds.Add(project.Id);
                }
            }

            if (_channels.EvaluateChannelAccessRule(channel, emp.Id, userProjectIds, userTeamIds, emp.IsAdmin))
            {
                validUserIds.Add(emp.Id);
            }
        }

        return validUserIds;
    }

    // ... (keep existing methods until UploadAttachmentAsync)

    // UploadAttachmentAsync moved to bottom to use FileService


    public async Task<ChatMessage?> SendMessageAsync(string senderId, string channelId, string content, string? replyToId = null)
    {
        return await SendMessageWithAttachmentsAsync(new SendMessageOptions
        {
            SenderId = senderId,
            ChannelId = channelId,
            Content = content,
            ReplyToId = replyToId
        });
    }

    public async Task<ChatMessage?> SendMessageAsync(SendMessageOptions options)
    {
        return await SendMessageWithAttachmentsAsync(options);
    }

    public async Task<ChatMessage?> SendCallInviteAsync(string senderId, string channelId)
    {
        var employee = _employees.GetById(senderId);
        var channel = _channels.GetById(channelId);
        if (employee == null || channel == null) return null;

        if (!CanUserPostToChannel(channel, employee))
        {
            _logger.LogWarning("SendCallInvite failed: User {UserId} denied post access to Announcement Channel {ChannelId}", employee.Id, channelId);
            return null;
        }

        var message = new ChatMessage
        {
            ChannelId = channelId,
            ChannelType = channel.ChannelType,
            SenderId = employee.Id,
            Content = $"🎙️ {employee.FullName} started a voice call.",
            MessageType = "CallInvite",
            CallStatus = "Active",
            SentAt = DateTime.UtcNow,
            ReadBy = new List<string> { employee.Id }
        };

        _messages.Save(message);
        _channels.UpdateLastActivity(channelId);
        _readStates.MarkAsRead(employee.Id, channelId, channel.ChannelType.ToString(), channel.IsVoiceChannel);

        _chatState.NotifyMessageReceived(message);

        await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("ReceiveMessage", new
        {
            message.Id,
            message.ChannelId,
            message.SenderId,
            SenderName = employee.FullName,
            message.Content,
            message.SentAt,
            message.ReplyToId,
            Attachments = new List<object>()
        });

        _ = _notificationRouting.RouteChatNotificationAsync(message, channel, employee);

        return message;
    }

    public async Task UpdateCallInviteStatusAsync(string messageId, string status)
    {
        var message = _messages.GetById(messageId);
        if (message == null || message.MessageType != "CallInvite") return;

        message.CallStatus = status;
        _messages.Save(message);

        _chatState.NotifyMessageEdited(message);

        await _hubContext.Clients.Group($"channel_{message.ChannelId}").SendAsync("MessageEdited", new
        {
            message.Id,
            message.ChannelId,
            message.Content,
            message.EditedAt,
            message.CallStatus
        });
    }

    [Obsolete("Use SendMessageWithAttachmentsAsync(SendMessageOptions options) instead.")]
    public async Task<ChatMessage?> SendMessageWithAttachmentsAsync(string senderId, string channelId, string content, List<ChatAttachment>? attachments = null, string? replyToId = null, string? privateKey = null)
    {
        return await SendMessageWithAttachmentsAsync(new SendMessageOptions
        {
            SenderId = senderId,
            ChannelId = channelId,
            Content = content,
            Attachments = attachments,
            ReplyToId = replyToId,
            PrivateKey = privateKey
        });
    }

    public async Task<ChatMessage?> SendMessageWithAttachmentsAsync(SendMessageOptions options)
    {
        var senderId = options.SenderId;
        var channelId = options.ChannelId;
        var content = options.Content;
        var attachments = options.Attachments;
        var replyToId = options.ReplyToId;
        var privateKey = options.PrivateKey;

        var employee = _employees.GetById(senderId);
        if (employee == null)
        {
            _logger.LogWarning("SendMessage failed: Employee not found: {SenderId}", senderId);
            return null;
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            _logger.LogWarning("SendMessage failed: Channel not found: {ChannelId}", channelId);
            return null;
        }

        // Validate Post Access
        if (!CanUserPostToChannel(channel, employee))
        {
            _logger.LogWarning("SendMessage failed: User {UserId} denied post access to Announcement Channel {ChannelId}", employee.Id, channelId);
            return null;
        }

        // Process Attachments - Save to Disk
        var processedAttachments = new List<ChatAttachment>();
        if (attachments != null)
        {
            foreach (var att in attachments)
            {
                // Check if it's base64 (starts with "data:")
                if (!string.IsNullOrEmpty(att.FilePath) && att.FilePath.StartsWith("data:"))
                {
                    try
                    {
                        var commaIndex = att.FilePath.IndexOf(',');
                        if (commaIndex >= 0)
                        {
                            var base64 = att.FilePath.Substring(commaIndex + 1);
                            var bytes = Convert.FromBase64String(base64);

                            // Delegate to unified FileService pipeline for secure storage and valid routing
                            using var stream = new MemoryStream(bytes);
                            var url = await _fileService.UploadStreamAsync("chat", channelId, stream, att.FileName);

                            // Update attachment to point to valid API URL returned by FileService
                            att.FilePath = url;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save attachment {FileName}", att.FileName);
                        continue; // Skip failed attachment
                    }
                }
                processedAttachments.Add(att);
            }
        }

        var modResult = await _moderationService.EvaluateTextAsync(content ?? "");
        if (modResult.HasViolation)
        {
            if (modResult.Action == TextModerationAction.Block)
            {
                _logger.LogWarning("SendMessage blocked due to moderation policy for User {UserId}", employee.Id);
                // Return null or maybe throw an exception if we want the client to know. For now returning null stops the send.
                return null;
            }
            else if (modResult.Action == TextModerationAction.Sanitize)
            {
                content = modResult.SanitizedText;
            }
        }

        var message = new ChatMessage
        {
            ChannelId = channelId,
            ChannelType = channel.ChannelType,
            SenderId = employee.Id,
            Content = content ?? "",
            ReplyToId = replyToId,
            SentAt = DateTime.UtcNow,
            Attachments = processedAttachments,
            ReadBy = new List<string> { employee.Id }
        };

        UpdateMessageMentionsAndTokens(message, channel, content, privateKey, isEdit: false);

        // --- ENCRYPTION (Data-at-Rest) ---
        if (channel.IsEncrypted)
        {
            if (string.IsNullOrEmpty(privateKey))
            {
                _logger.LogWarning("SendMessage failed: Vault locked for User {UserId}", employee.Id);
                return null; // Don't save if we can't encrypt!
            }

            if (channel.EncryptedChannelKeys.TryGetValue(employee.Id, out var cipherChannelKey))
            {
                try
                {
                    var plainChannelKey = _crypto.DecryptRsa(cipherChannelKey, privateKey);
                    message.Content = _crypto.EncryptAes(message.Content, plainChannelKey);
                    message.IsEncrypted = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SendMessage failed: RSA Decryption of Channel Key failed for User {UserId}", employee.Id);
                    return null;
                }
            }
            else
            {
                _logger.LogWarning("SendMessage failed: Channel Key not found for User {UserId}", employee.Id);
                return null;
            }
        }

        // Save to database
        try
        {
            _messages.Save(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save message {MessageId}", message.Id);
            return null;
        }

        // Update channel last activity
        _channels.UpdateLastActivity(channelId);
        _readStates.MarkAsRead(employee.Id, channelId, channel.ChannelType.ToString(), channel.IsVoiceChannel);

        // Auto-mark previous messages in this channel as read by the sender
        try
        {
            var recentMessages = _messages.GetByChannel(channelId, 50);
            foreach (var prevMsg in recentMessages)
            {
                if (prevMsg.Id != message.Id && !prevMsg.ReadBy.Contains(employee.Id))
                {
                    prevMsg.ReadBy.Add(employee.Id);
                    _messages.Save(prevMsg);
                    _chatState.NotifyMessageRead(channelId, prevMsg.Id, prevMsg.ReadBy);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-mark previous messages as read for sender {SenderId} in channel {ChannelId}", employee.Id, channelId);
        }

        // 1. Notify local subscribers (Server-side Blazor)
        _chatState.NotifyMessageReceived(message);

        // 2. Broadcast to external clients via SignalR
        await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("ReceiveMessage", new
        {
            message.Id,
            message.ChannelId,
            message.SenderId,
            SenderName = employee.FullName,
            message.Content,
            message.SentAt,
            message.ReplyToId,
            Attachments = message.Attachments.Select(a => new { a.Id, a.FileName, a.FilePath, a.ContentType, a.FileSizeBytes })
        });

        // 3. Delegate routing rules to the central notification routing service
        _ = _notificationRouting.RouteChatNotificationAsync(message, channel, employee);

        return message;
    }

    public async Task MarkMessageAsReadAsync(string channelId, string messageId, string userId)
    {
        var message = _messages.GetById(messageId);
        if (message == null || message.ChannelId != channelId) return;

        if (!message.ReadBy.Contains(userId))
        {
            message.ReadBy.Add(userId);
            _messages.Save(message);

            // Notify local subscribers
            _chatState.NotifyMessageRead(channelId, messageId, message.ReadBy);

            // SignalR
            await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("MessageRead", new
            {
                ChannelId = channelId,
                MessageId = messageId,
                ReadBy = message.ReadBy
            });
        }
    }

    public async Task MarkChannelMessagesAsReadAsync(string channelId, string userId, int count = 50)
    {
        var messages = _messages.GetByChannel(channelId, count);
        var updated = false;

        foreach (var msg in messages)
        {
            if (msg.SenderId != userId && !msg.ReadBy.Contains(userId))
            {
                msg.ReadBy.Add(userId);
                _messages.Save(msg);
                _chatState.NotifyMessageRead(channelId, msg.Id, msg.ReadBy);
                updated = true;
            }
        }

        if (updated)
        {
            await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("ChannelMessagesRead", new
            {
                ChannelId = channelId,
                UserId = userId
            });
        }
    }


    public async Task StartTypingAsync(string userId, string channelId)
    {
        var employee = _employees.GetById(userId);
        if (employee == null) return;

        // Local
        _chatState.NotifyUserTyping(channelId, employee.Id, employee.FullName);
        _presenceState.PingUserActive(employee.Id);

        // SignalR
        await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("UserTyping", new
        {
            ChannelId = channelId,
            UserId = employee.Id,
            UserName = employee.FullName
        });
    }

    public async Task StopTypingAsync(string userId, string channelId)
    {
        // Local
        _chatState.NotifyUserStoppedTyping(channelId, userId);

        // SignalR
        await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("UserStoppedTyping", new
        {
            ChannelId = channelId,
            UserId = userId
        });
    }

    public async Task EditMessageAsync(string userId, string messageId, string newContent, string? requestingUserPrivateKey = null)
    {
        var message = _messages.GetById(messageId);
        if (message == null || message.SenderId != userId) return;

        var channel = _channels.GetById(message.ChannelId);
        var employee = _employees.GetById(userId);
        if (channel != null && employee != null && !CanUserPostToChannel(channel, employee))
        {
            _logger.LogWarning("EditMessage failed: User {UserId} denied post access to Announcement Channel {ChannelId}", employee.Id, channel.Id);
            return;
        }
        message.EditedAt = DateTime.UtcNow;

        if (channel != null)
        {
            // Pass the original plaintext 'newContent' to the parser, 
            // since message.Content might be encrypted below
            var plainContent = newContent;
            
            if (channel.IsEncrypted && message.IsEncrypted)
            {
                if (string.IsNullOrEmpty(requestingUserPrivateKey))
                {
                    return;
                }
                if (channel.EncryptedChannelKeys.TryGetValue(userId, out var cipherChannelKey))
                {
                    try
                    {
                        var plainChannelKey = _crypto.DecryptRsa(cipherChannelKey, requestingUserPrivateKey);
                        newContent = _crypto.EncryptAes(newContent, plainChannelKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to re-encrypt message content during edit.");
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            message.Content = newContent;
            UpdateMessageMentionsAndTokens(message, channel, plainContent, requestingUserPrivateKey, isEdit: true);
        }
        else
        {
            message.Content = newContent;
        }

        _messages.Save(message);

        // Local
        _chatState.NotifyMessageEdited(message);

        // SignalR
        await _hubContext.Clients.Group($"channel_{message.ChannelId}").SendAsync("MessageEdited", new
        {
            message.Id,
            message.ChannelId,
            message.Content,
            message.EditedAt
        });
    }

    private void UpdateMessageMentionsAndTokens(ChatMessage message, ChatChannel channel, string plainContent, string? privateKey, bool isEdit)
    {
        var oldUserIds = message.MentionedUserIds.ToList();
        var oldEventIds = message.MentionedEventIds.ToList();
        var oldAlbumIds = message.MentionedAlbumIds.ToList();
        var oldMentionsTeam = message.MentionsTeam;
        var oldMentionsEveryone = message.MentionsEveryone;

        message.MentionedUserIds.Clear();
        message.MentionedEventIds.Clear();
        message.MentionedAlbumIds.Clear();
        message.MentionsTeam = false;
        message.MentionsEveryone = false;

        if (!string.IsNullOrEmpty(plainContent))
        {
            var mentionMatches = System.Text.RegularExpressions.Regex.Matches(plainContent, @"@\[([^\]]+)\]");
            foreach (System.Text.RegularExpressions.Match match in mentionMatches)
            {
                var displayName = match.Groups[1].Value;

                var mentionedEmployee = _employees.GetAll()
                    .FirstOrDefault(e => e.FullName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
                if (mentionedEmployee != null)
                {
                    if (!message.MentionedUserIds.Contains(mentionedEmployee.Id))
                        message.MentionedUserIds.Add(mentionedEmployee.Id);
                    continue;
                }

                var team = _teams.GetAll()
                    .FirstOrDefault(t => t.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase));
                if (team != null)
                {
                    message.MentionsTeam = true;
                    var teamMembers = _employees.GetAll().Where(e => e.TeamId == team.Id);
                    foreach (var member in teamMembers)
                    {
                        if (!message.MentionedUserIds.Contains(member.Id))
                            message.MentionedUserIds.Add(member.Id);
                    }
                }
            }

            message.MentionsEveryone = System.Text.RegularExpressions.Regex.IsMatch(plainContent, @"@everyone\b");

            var eventMatches = System.Text.RegularExpressions.Regex.Matches(plainContent, @"#\[([^\]]+)\]\(event:([^\)]+)\)");
            foreach (System.Text.RegularExpressions.Match match in eventMatches)
            {
                var eventId = match.Groups[2].Value;
                if (!message.MentionedEventIds.Contains(eventId))
                    message.MentionedEventIds.Add(eventId);
            }

            var albumMatches = System.Text.RegularExpressions.Regex.Matches(plainContent, @"#\[([^\]]+)\]\(album:([^\)]+)\)");
            foreach (System.Text.RegularExpressions.Match match in albumMatches)
            {
                var albumId = match.Groups[2].Value;
                if (!message.MentionedAlbumIds.Contains(albumId))
                {
                    message.MentionedAlbumIds.Add(albumId);
                }
            }
        }

        var newAlbumIds = message.MentionedAlbumIds.Except(oldAlbumIds).ToList();
        foreach (var albumId in newAlbumIds)
        {
            _albumService.ShareAlbumWithChannel(albumId, message.SenderId, channel.Id, privateKey);
        }

        var removedAlbumIds = oldAlbumIds.Except(message.MentionedAlbumIds).ToList();
        foreach (var albumId in removedAlbumIds)
        {
            var otherMentionsExist = false;
            if (_messages is ChatMessageRepository chatRepo)
            {
                var index = chatRepo.GetIndexForChannel(channel.Id);
                foreach (var entry in index)
                {
                    if (entry.Id != message.Id && !entry.IsDeleted)
                    {
                        var msg = chatRepo.GetMessageFromDisk(channel.Id, entry.Id);
                        if (msg != null && msg.MentionedAlbumIds.Contains(albumId))
                        {
                            otherMentionsExist = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                otherMentionsExist = _messages.GetAll()
                    .Any(m => m.ChannelId == channel.Id && m.Id != message.Id && !m.IsDeleted && m.MentionedAlbumIds.Contains(albumId));
            }

            if (!otherMentionsExist)
            {
                _albumService.UnshareAlbumWithChannel(albumId, message.SenderId, channel.Id);
            }
        }

        if (isEdit)
        {
            var newlyMentionedUserIds = message.MentionedUserIds.Except(oldUserIds).ToList();
            var newlyMentionedTeam = !oldMentionsTeam && message.MentionsTeam;
            var newlyMentionedEveryone = !oldMentionsEveryone && message.MentionsEveryone;

            if (newlyMentionedUserIds.Any() || newlyMentionedTeam || newlyMentionedEveryone)
            {
                var sender = _employees.GetById(message.SenderId);
                if (sender != null)
                {
                    var notificationMessage = new ChatMessage
                    {
                        Id = message.Id,
                        ChannelId = message.ChannelId,
                        SenderId = message.SenderId,
                        Content = message.Content,
                        IsEncrypted = message.IsEncrypted,
                        MentionedUserIds = newlyMentionedUserIds,
                        MentionsTeam = newlyMentionedTeam,
                        MentionsEveryone = newlyMentionedEveryone
                    };

                    _ = _notificationRouting.RouteChatNotificationAsync(notificationMessage, channel, sender);
                }
            }
        }
    }

    public async Task AdminDeleteMessageAsync(string messageId)
    {
        var message = _messages.GetById(messageId);
        if (message == null) return;

        if (message.Attachments != null && message.Attachments.Any())
        {
            foreach (var att in message.Attachments)
            {
                if (!string.IsNullOrEmpty(att.FilePath))
                {
                    try
                    {
                        await _fileService.DeleteAsync(att.FilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AdminDeleteMessageAsync: Failed to delete attachment {FilePath}", att.FilePath);
                    }
                }
            }
            message.Attachments.Clear();
        }

        message.IsDeleted = true;
        message.Content = "content deleted";
        _messages.Save(message);

        // Local
        _chatState.NotifyMessageDeleted(message.ChannelId, messageId);

        // SignalR
        await _hubContext.Clients.Group($"channel_{message.ChannelId}").SendAsync("MessageDeleted", new
        {
            message.Id,
            message.ChannelId
        });
    }

    public async Task DeleteMessageAsync(string userId, string messageId)
    {
        var message = _messages.GetById(messageId);
        if (message == null || message.SenderId != userId) return;

        message.IsDeleted = true;
        _messages.Save(message);

        // Local
        _chatState.NotifyMessageDeleted(message.ChannelId, messageId);

        // SignalR
        await _hubContext.Clients.Group($"channel_{message.ChannelId}").SendAsync("MessageDeleted", new
        {
            message.Id,
            message.ChannelId
        });
    }

    public async Task ToggleReactionAsync(string userId, string messageId, string emoji)
    {
        var employee = _employees.GetById(userId);
        if (employee == null) return;

        var message = _messages.GetById(messageId);
        if (message == null) return;

        var channel = _channels.GetById(message.ChannelId);
        if (channel != null && !CanUserPostToChannel(channel, employee))
        {
            _logger.LogWarning("ToggleReaction failed: User {UserId} denied post access to Announcement Channel {ChannelId}", employee.Id, channel.Id);
            return;
        }

        var reactionKey = $"{emoji}:{userId}";

        if (message.Reactions.Contains(reactionKey))
        {
            // Remove the reaction
            message.Reactions.Remove(reactionKey);
            _messages.Save(message);

            // Local
            _chatState.NotifyReactionRemoved(message.ChannelId, message.Id, emoji, employee.Id);

            // SignalR
            await _hubContext.Clients.Group($"channel_{message.ChannelId}").SendAsync("ReactionRemoved", new
            {
                MessageId = message.Id,
                message.ChannelId,
                Emoji = emoji,
                UserId = employee.Id
            });
        }
        else
        {
            // Add the reaction
            message.Reactions.Add(reactionKey);
            _messages.Save(message);

            // Local
            _chatState.NotifyReactionAdded(message.ChannelId, message.Id, emoji, employee.Id, employee.FullName);

            // SignalR
            await _hubContext.Clients.Group($"channel_{message.ChannelId}").SendAsync("ReactionAdded", new
            {
                MessageId = message.Id,
                message.ChannelId,
                Emoji = emoji,
                UserId = employee.Id,
                UserName = employee.FullName
            });

            // Push Notifications
            if (channel != null)
            {
                _ = _notificationRouting.RouteReactionNotificationAsync(message, channel, emoji, employee);
            }
        }
    }


    public async Task<ChatChannel> CreateDirectMessageChannelAsync(string initiatorUserId, string targetUserId, bool isEncrypted = false)
    {
        if (isEncrypted)
        {
            var p1 = _employees.GetById(initiatorUserId);
            var p2 = _employees.GetById(targetUserId);

            if (p1 == null || !p1.HasChatPassword)
                throw new InvalidOperationException($"Cannot encrypt chat: User '{p1?.FullName ?? "Unknown"}' has not set up their Vault Password yet.");

            if (p2 == null || !p2.HasChatPassword)
                throw new InvalidOperationException($"Cannot encrypt chat: User '{p2?.FullName ?? "Unknown"}' has not set up their Vault Password yet.");
        }

        // 1. Create locally (DB) always unencrypted first to ensure base channel exists cleanly
        var channel = _channels.GetOrCreateDirectChannel(initiatorUserId, targetUserId, false, createdById: initiatorUserId);

        if (isEncrypted && !channel.IsEncrypted)
        {
            // This will generate the keys and save the channel state correctly
            await ToggleChannelEncryptionAsync(channel.Id, true, initiatorUserId);
            // Re-fetch modifying the local ref
            channel = _channels.GetById(channel.Id)!;
        }

        // 2. Notify local subscribers
        _chatState.NotifyChannelCreated(channel);

        // 3. Optional: Broadcast to SignalR if we want external clients (e.g. mobile app) to see it instantly too
        // For now, focusing on Server-side Blazor connectivity via ChatStateService.

        return channel;
    }

    public string GetChannelDisplayName(ChatChannel channel, string? currentUserId, string youSuffix = ChatChannelFormatter.DefaultYouSuffix)
    {
        return ChatChannelFormatter.GetDisplayName(channel, currentUserId, id => _employees.GetById(id), youSuffix);
    }

    public bool IsSelfDirectMessage(ChatChannel? channel, string? currentUserId)
    {
        return ChatChannelFormatter.IsSelfDirectMessage(channel, currentUserId);
    }

    public async Task<ChatChannel> CreateGroupDirectMessageChannelAsync(string initiatorUserId, List<string> targetUserIds, bool isEncrypted = false)
    {
        var allParticipantIds = new List<string> { initiatorUserId };
        allParticipantIds.AddRange(targetUserIds);
        allParticipantIds = allParticipantIds.Distinct().ToList();

        if (isEncrypted)
        {
            foreach (var uid in allParticipantIds)
            {
                var p = _employees.GetById(uid);
                if (p == null || !p.HasChatPassword)
                    throw new InvalidOperationException($"Cannot encrypt chat: User '{p?.FullName ?? "Unknown"}' has not set up their Vault Password yet.");
            }
        }

        // 1. Create locally (DB) always unencrypted first
        var channel = _channels.CreateGroupChannel(allParticipantIds, "", false, createdById: initiatorUserId);

        if (isEncrypted && !channel.IsEncrypted)
        {
            await ToggleChannelEncryptionAsync(channel.Id, true, initiatorUserId);
            channel = _channels.GetById(channel.Id)!;
        }

        // 2. Notify local subscribers
        _chatState.NotifyChannelCreated(channel);

        return channel;
    }

    public async Task<ChatChannel> CreateTeamChannelAsync(string initiatorUserId, List<string> teamIds, bool isEncrypted = false)
    {
        // Build default name from team names
        var teamNames = teamIds
            .Select(id => _teams.GetById(id))
            .Where(t => t != null)
            .Select(t => t!.Name)
            .ToList();
        var defaultName = string.Join(", ", teamNames);

        var channel = new ChatChannel
        {
            Name = defaultName,
            ChannelType = ChatChannelType.Team,
            AllowedTeamIds = teamIds,
            ParticipantIds = new List<string>(),
            CreatedById = initiatorUserId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            IsEncrypted = false
        };

        _channels.Save(channel);

        if (isEncrypted)
        {
            await ToggleChannelEncryptionAsync(channel.Id, true, initiatorUserId);
            channel = _channels.GetById(channel.Id)!;
        }

        _chatState.NotifyChannelCreated(channel);
        return channel;
    }

    public async Task<ChatChannel> CreateChannelAsync(ChatChannel channel)
    {
        channel.Normalize();
        var wasEncrypted = channel.IsEncrypted;
        channel.IsEncrypted = false; // Always save unencrypted first to ensure base channel exists cleanly

        // 1. Save to DB
        _channels.Save(channel);

        if (wasEncrypted)
        {
            // Toggle encryption after creation to generate keys correctly
            await ToggleChannelEncryptionAsync(channel.Id, true, channel.CreatedById);
            channel = _channels.GetById(channel.Id)!;
        }

        // 2. Notify local subscribers
        _chatState.NotifyChannelCreated(channel);

        // 3. Broadcast to relevant users via SignalR if needed
        // For now, we rely on ChatStateService for server-side blazor updates.

        return channel;
    }

    public async Task UpdateChannelAsync(ChatChannel channel)
    {
        channel.Normalize();

        // 1. Save to DB
        _channels.Save(channel);

        // 2. Notify local subscribers - Reusing ChannelCreated for now as it forces reload in UI
        // Or we could add a specific ChannelUpdated event
        _chatState.NotifyChannelCreated(channel);

        await Task.CompletedTask;
    }

    public async Task JoinVoiceAsync(string userId, string channelId)
    {
        _chatState.NotifyVoiceMemberJoined(channelId, userId);
        await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("VoiceMemberJoined", channelId, userId);
    }

    public async Task LeaveVoiceAsync(string userId, string channelId)
    {
        _chatState.NotifyVoiceMemberLeft(channelId, userId);

        var participants = _chatState.GetVoiceParticipants(channelId);
        if (participants.Count == 0)
        {
            var activeInvite = _messages.GetByChannel(channelId, 50)
                .FirstOrDefault(m => m.MessageType == "CallInvite" && m.CallStatus == "Active");
            if (activeInvite != null)
            {
                await UpdateCallInviteStatusAsync(activeInvite.Id, "Ended");
            }
        }

        // --- Explicitly evict the user from the LiveKit room to prevent malicious WebRTC persistence ---
        try
        {
            var config = _systemConfigs?.Get();
            if (config != null && !string.IsNullOrEmpty(config.LiveKitApiKey) && !string.IsNullOrEmpty(config.LiveKitApiSecret))
            {
                var voicePort = Environment.GetEnvironmentVariable("Spokes_Voice_Port") ?? "7880";
                var liveKitUrl = $"http://127.0.0.1:{voicePort}";
                var roomServiceClient = new Livekit.Server.Sdk.Dotnet.RoomServiceClient(liveKitUrl, config.LiveKitApiKey, config.LiveKitApiSecret);
                
                var roomName = $"channel-{channelId}";
                await roomServiceClient.RemoveParticipant(new Livekit.Server.Sdk.Dotnet.RoomParticipantIdentity {
                    Room = roomName,
                    Identity = userId
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[ChatService] Failed to evict user {userId} from LiveKit room channel-{channelId}: {ex.Message}");
        }
        // ------------------------------------------------------------------------------------------------

        await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("VoiceMemberLeft", channelId, userId);
    }

    public async Task UpdateActiveSpeakersAsync(string channelId, string[] speakerIds)
    {
        _chatState.NotifyVoiceActiveSpeakersChanged(channelId, speakerIds);
        await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("VoiceActiveSpeakersChanged", channelId, speakerIds);
    }

    public async Task UpdateChannelOrdersAsync(Dictionary<string, int> channelOrders)
    {
        foreach (var kvp in channelOrders)
        {
            var channel = await _channels.GetByIdAsync(kvp.Key);
            if (channel != null)
            {
                channel.DisplayOrder = kvp.Value;
                await _channels.SaveAsync(channel);
                _chatState.NotifyChannelCreated(channel);
            }
        }
    }
    public async Task ToggleChannelEncryptionAsync(string channelId, bool enableEncryption, string requestingUserId, string? privateKeyContext = null)
    {
        var channel = _channels.GetById(channelId);
        if (channel == null || channel.IsEncrypted == enableEncryption) return;

        var isDMRestricted = channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group || channel.ParticipantIds.Any();

        string? existingCipherKeyForDisable = null;

        if (enableEncryption)
        {
            if (isDMRestricted)
            {
                foreach (var uid in channel.ParticipantIds)
                {
                    var u = _employees.GetById(uid);
                    if (u == null || !u.HasChatPassword)
                    {
                        var name = u?.FullName ?? "Unknown User";
                        throw new InvalidOperationException($"Cannot encrypt chat: User '{name}' has not set up their Vault Password yet.");
                    }
                }
            }
        }
        else
        {
            if (string.IsNullOrEmpty(privateKeyContext))
            {
                throw new InvalidOperationException("Private Key is required to disable encryption.");
            }
            if (!channel.EncryptedChannelKeys.TryGetValue(requestingUserId, out existingCipherKeyForDisable))
            {
                throw new InvalidOperationException("Could not find your decryption key to disable encryption. Your session may be out of date.");
            }
        }

        // Ensure we save the channel's intent right away so UI knows
        channel.IsEncrypted = enableEncryption;
        if (!enableEncryption) channel.EncryptedChannelKeys.Clear();
        _channels.Save(channel);

        await Task.Run(async () =>
        {
            try
            {
                List<ChatMessage> allMessages = new List<ChatMessage>();
                if (_messages is Spokes_Server.Core.Data.Repositories.Communication.ChatMessageRepository chatRepo)
                {
                    var index = chatRepo.GetIndexForChannel(channelId);
                    foreach (var entry in index)
                    {
                        var msg = chatRepo.GetMessageFromDisk(channelId, entry.Id);
                        if (msg != null) allMessages.Add(msg);
                    }
                }
                else
                {
                    allMessages = _messages.GetAll().Where(m => m.ChannelId == channelId).ToList();
                }

                if (enableEncryption)
                {
                    var newChannelKey = _crypto.GenerateAesKeyBase64();
                    var allowedUserIds = new HashSet<string>();

                    if (channel.ParticipantIds.Any())
                    {
                        foreach (var p in channel.ParticipantIds) allowedUserIds.Add(p);
                    }
                    else
                    {
                        var members = _employees.GetAll().Where(e => e.IsActive && e.HasChatPassword);

                        if (channel.ChannelType == ChatChannelType.Team)
                        {
                            var team = _teams.GetById(channel.LinkedEntityId ?? "");
                            if (team != null)
                            {
                                members = members.Where(e => e.TeamId == team.Id || team.LeaderId == e.Id);
                            }
                        }
                        else if (channel.ChannelType == ChatChannelType.Project)
                        {
                            var proj = _projects.GetById(channel.LinkedEntityId ?? "");
                            if (proj != null && proj.AccessPolicy != "Public")
                            {
                                members = members.Where(e => e.IsAdmin || proj.AllowedUserIds.Contains(e.Id) || (e.TeamId != null && proj.AllowedTeamIds.Contains(e.TeamId)));
                            }
                        }
                        else if (channel.AllowedTeamIds.Any())
                        {
                            members = members.Where(e => channel.AllowedTeamIds.Contains(e.TeamId));
                        }

                        foreach (var m in members) allowedUserIds.Add(m.Id);
                    }

                    channel.EncryptedChannelKeys.Clear();
                    foreach (var uid in allowedUserIds)
                    {
                        var user = _employees.GetById(uid);
                        if (user != null && !string.IsNullOrEmpty(user.PublicKey))
                        {
                            channel.EncryptedChannelKeys[uid] = _crypto.EncryptRsa(newChannelKey, user.PublicKey);
                        }
                    }

                    // Escrow for subsequent unauthenticated users if public/team
                    if (!isDMRestricted && _escrowService.IsEscrowAvailable)
                    {
                        channel.EncryptedChannelKeys["ServerMaster"] = _crypto.EncryptRsa(newChannelKey, _escrowService.EscrowPublicKey!);
                    }

                    foreach (var m in allMessages)
                    {
                        if (!m.IsEncrypted && !string.IsNullOrEmpty(m.Content))
                        {
                            m.Content = _crypto.EncryptAes(m.Content, newChannelKey);
                            m.IsEncrypted = true;
                            _messages.Save(m);
                        }
                    }

                    _albumService.ReEvaluateAlbumEncryptionForChannel(channelId, newChannelKey);
                }
                else
                {
                    if (string.IsNullOrEmpty(existingCipherKeyForDisable)) return;

                    try
                    {
                        var plainChannelKey = _crypto.DecryptRsa(existingCipherKeyForDisable, privateKeyContext!);

                        foreach (var m in allMessages)
                        {
                            if (m.IsEncrypted && !string.IsNullOrEmpty(m.Content))
                            {
                                try
                                {
                                    m.Content = _crypto.DecryptAes(m.Content, plainChannelKey);
                                    m.IsEncrypted = false;
                                    _messages.Save(m);
                                }
                                catch (Exception ex)
                                {
                                    // Ignore AES error for a specific message, but do NOT flip IsEncrypted = false!
                                    // Otherwise it gets permanently corrupted in the UI.
                                    _logger.LogWarning(ex, "Failed to decrypt message {MessageId} during encryption toggle.", m.Id);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Decryption error converting channel {ChannelId} back to plaintext.", channelId);
                    }
                }

                _channels.Save(channel);
                await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("ChannelUpdated", channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling channel encryption for {ChannelId}", channelId);
            }
        });
    }

    /// <summary>
    /// Called when a user sets up their Chat Password for the first time.
    /// Uses the Server Escrow key to decrypt AES channel keys from Public/Team channels
    /// and re-encrypts them with the user's new Public Key.
    /// </summary>
    public async Task ClaimEscrowedKeysAsync(string userId)
    {
        if (!_escrowService.IsEscrowAvailable) return;

        var user = await _employees.GetByIdAsync(userId);
        if (user == null || string.IsNullOrEmpty(user.PublicKey)) return;

        var allChannels = (await _channels.GetAllAsync()).Where(c => c.IsEncrypted).ToList();

        foreach (var channel in allChannels)
        {
            // Skip if user already has a key for this channel
            if (channel.EncryptedChannelKeys.ContainsKey(userId)) continue;

            // Only claim from ServerMaster escrow (public/team channels)
            if (!channel.EncryptedChannelKeys.TryGetValue("ServerMaster", out var encryptedEscrowKey)) continue;

            // Check if user should have access to this channel
            bool hasAccess = false;
            if (channel.ChannelType == ChatChannelType.General)
            {
                if (channel.IsDefaultGeneral) hasAccess = true;
                else if (!channel.ParticipantIds.Any() && !channel.AllowedTeamIds.Any()) hasAccess = true;
                else if (channel.ParticipantIds.Contains(userId) || (user.TeamId != null && channel.AllowedTeamIds.Contains(user.TeamId))) hasAccess = true;
            }
            else if (channel.ChannelType == ChatChannelType.Team)
            {
                if (channel.ParticipantIds.Contains(userId))
                {
                    hasAccess = true;
                }
                else if (user.TeamId != null && channel.AllowedTeamIds.Contains(user.TeamId))
                {
                    hasAccess = true;
                }
                else
                {
                    var teams = (await _teams.GetAllAsync()).Where(t => channel.AllowedTeamIds.Contains(t.Id)).ToList();
                    if (teams.Any(t => t.LeaderId == userId))
                        hasAccess = true;
                }
            }
            else if (channel.ChannelType == ChatChannelType.Project)
            {
                var proj = (await _projects.GetAllAsync()).FirstOrDefault(p => p.Id == channel.LinkedEntityId);
                if (proj != null)
                {
                    if (proj.AccessPolicy == "Public") hasAccess = true;
                    else if (user.IsAdmin || proj.AllowedUserIds.Contains(userId) || (user.TeamId != null && proj.AllowedTeamIds.Contains(user.TeamId))) hasAccess = true;
                }
            }

            if (!hasAccess) continue;

            try
            {
                var plainChannelKey = _crypto.DecryptRsa(encryptedEscrowKey, _escrowService.DecryptedEscrowPrivateKey!);
                channel.EncryptedChannelKeys[userId] = _crypto.EncryptRsa(plainChannelKey, user.PublicKey);
                await _channels.SaveAsync(channel);
                
                await _hubContext.Clients.Group($"channel_{channel.Id}").SendAsync("ChannelUpdated", channel);
                
                _logger.LogInformation("Claimed escrow key for user {UserId} in channel {ChannelId}", userId, channel.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to claim escrow for user {UserId} in channel {ChannelId}", userId, channel.Id);
            }
        }
    }

    /// <summary>
    /// Implicitly shares the decrypted channel AES key with any newly-keyed participants
    /// whenever an authorized current participant reads or writes to the encrypted channel.
    /// Acts as a decentralized alternative when Server Escrow is disabled.
    /// </summary>
    public Task ShareKeyOpportunisticallyAsync(string channelId, string plainChannelKey)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var channel = _channels.GetById(channelId);
                if (channel == null || !channel.IsEncrypted) return;

                bool modified = false;

                // Heal ServerMaster if Escrow is now available but was missing when encrypted
                if (_escrowService.IsEscrowAvailable && !channel.EncryptedChannelKeys.ContainsKey("ServerMaster"))
                {
                    bool isDMRestricted = channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group;
                    if (!isDMRestricted && !string.IsNullOrEmpty(_escrowService.EscrowPublicKey))
                    {
                        channel.EncryptedChannelKeys["ServerMaster"] = _crypto.EncryptRsa(plainChannelKey, _escrowService.EscrowPublicKey);
                        modified = true;
                        _logger.LogInformation("Opportunistically healed ServerMaster escrow key for channel {ChannelId}", channelId);
                    }
                }

                var allActiveKeyedUsers = _employees.GetAll()
                    .Where(e => e.IsActive && e.HasChatPassword && !string.IsNullOrEmpty(e.PublicKey))
                    .ToList();

                foreach (var u in allActiveKeyedUsers)
                {
                    // Skip if user already has a key for this channel
                    if (channel.EncryptedChannelKeys.ContainsKey(u.Id)) continue;

                    bool hasAccess = false;

                    if (channel.ChannelType == ChatChannelType.General)
                    {
                        if (channel.IsDefaultGeneral) hasAccess = true;
                        else if (!channel.ParticipantIds.Any() && !channel.AllowedTeamIds.Any()) hasAccess = true;
                        else if (channel.ParticipantIds.Contains(u.Id) || (u.TeamId != null && channel.AllowedTeamIds.Contains(u.TeamId))) hasAccess = true;
                    }
                    else if (channel.ChannelType == ChatChannelType.Project)
                    {
                        var pId = channel.LinkedEntityId;
                        if (!string.IsNullOrEmpty(pId))
                        {
                            var proj = _projects.GetById(pId);
                            if (proj != null)
                            {
                                if (u.IsAdmin || proj.AccessPolicy == "Public")
                                {
                                    hasAccess = true;
                                }
                                else if (proj.AllowedUserIds.Contains(u.Id) || (u.TeamId != null && proj.AllowedTeamIds.Contains(u.TeamId)))
                                {
                                    hasAccess = true;
                                }
                            }
                        }
                    }
                    else if (channel.ChannelType == ChatChannelType.Team)
                    {
                        if (channel.ParticipantIds.Contains(u.Id))
                        {
                            hasAccess = true;
                        }
                        else if (u.TeamId != null && channel.AllowedTeamIds.Contains(u.TeamId))
                        {
                            hasAccess = true;
                        }
                        else
                        {
                            var teams = _teams.GetAll().Where(t => channel.AllowedTeamIds.Contains(t.Id)).ToList();
                            if (teams.Any(t => t.LeaderId == u.Id))
                                hasAccess = true;
                        }
                    }
                    else if (channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group)
                    {
                        if (channel.ParticipantIds.Contains(u.Id)) hasAccess = true;
                    }

                    if (hasAccess)
                    {
                        channel.EncryptedChannelKeys[u.Id] = _crypto.EncryptRsa(plainChannelKey, u.PublicKey!);
                        modified = true;
                        _logger.LogInformation("Opportunistically shared channel {Channel} key with user {User}", channelId, u.Id);
                    }
                }

                if (modified)
                {
                    _channels.Save(channel);
                    _hubContext.Clients.Group($"channel_{channelId}").SendAsync("ChannelUpdated", channel);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to opportunistically share key for channel {ChannelId}", channelId);
            }
        });

        return Task.CompletedTask;
    }

    public async Task<List<ChatMessage>> SecureSearchMessagesAsync(string channelId, string query, string requestingUserId, string? requestingUserPrivateKey = null)
    {
        var channel = _channels.GetById(channelId);
        if (channel == null) return new List<ChatMessage>();

        // 1. Verify access (basic scope test)
        if (channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group)
        {
            if (!channel.ParticipantIds.Contains(requestingUserId))
                return new List<ChatMessage>();
        }

        var results = new List<ChatMessage>();
        var lowerQuery = query.ToLowerInvariant();
        string? plainChannelKey = null;

        if (channel.IsEncrypted)
        {
            bool keyFound = false;

            // 1. Try to use user's own private key if provided (required for DMs)
            if (!string.IsNullOrEmpty(requestingUserPrivateKey) &&
                channel.EncryptedChannelKeys.TryGetValue(requestingUserId, out var userCipherKey))
            {
                try
                {
                    plainChannelKey = _crypto.DecryptRsa(userCipherKey, requestingUserPrivateKey);
                    keyFound = true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to decrypt channel key with user private key. Falling back to escrow.");
                }
            }

            // 2. Fallback to Escrow if available (for Public/Team channels where user might not have their own key set up yet)
            if (!keyFound && _escrowService.IsEscrowAvailable &&
                channel.EncryptedChannelKeys.TryGetValue("ServerMaster", out var escrowCipherKey))
            {
                try
                {
                    plainChannelKey = _crypto.DecryptRsa(escrowCipherKey, _escrowService.DecryptedEscrowPrivateKey!);
                    keyFound = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt channel key using escrow key.");
                }
            }

            if (!keyFound) return new List<ChatMessage>();
        }

        if (_messages is Spokes_Server.Core.Data.Repositories.Communication.ChatMessageRepository chatRepo)
        {
            var index = chatRepo.GetIndexForChannel(channelId);
            // Sort index by SentAt desc to search newest first
            foreach (var entry in index.OrderByDescending(e => e.SentAt))
            {
                if (entry.IsDeleted) continue;

                // Fast path for unencrypted messages via index
                if (!entry.IsEncrypted && !channel.IsEncrypted && entry.SearchText != null)
                {
                    if (entry.SearchText.Contains(lowerQuery))
                    {
                        var msg = await chatRepo.GetMessageFromDiskAsync(channelId, entry.Id);
                        if (msg != null) results.Add(msg);
                    }
                }
                else if (channel.IsEncrypted && entry.IsEncrypted && plainChannelKey != null)
                {
                    // Streaming decryption search
                    var msg = await chatRepo.GetMessageFromDiskAsync(channelId, entry.Id);
                    if (msg != null && !string.IsNullOrEmpty(msg.Content))
                    {
                        try
                        {
                            var plainContent = _crypto.DecryptAes(msg.Content, plainChannelKey);
                            if (plainContent.ToLowerInvariant().Contains(lowerQuery))
                            {
                                results.Add(new ChatMessage
                                {
                                    Id = msg.Id,
                                    ChannelId = msg.ChannelId,
                                    SenderId = msg.SenderId,
                                    SentAt = msg.SentAt,
                                    Content = plainContent,
                                    IsEncrypted = false // For UI highlighting
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to decrypt message {MessageId} during search", msg.Id);
                        }
                    }
                }

                if (results.Count >= 100) break;
            }
        }
        else
        {
            // Fallback for non-JsonRepository (tests etc)
            var allMessages = _messages.GetAll()
                .Where(m => m.ChannelId == channelId && !m.IsDeleted)
                .OrderByDescending(m => m.SentAt);

            foreach (var m in allMessages)
            {
                var contentToSearch = m.Content ?? "";
                if (channel.IsEncrypted && m.IsEncrypted && !string.IsNullOrEmpty(contentToSearch))
                {
                    if (plainChannelKey != null)
                    {
                        try { contentToSearch = _crypto.DecryptAes(contentToSearch, plainChannelKey); }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to decrypt legacy message {MessageId} during search.", m.Id);
                            continue;
                        }
                    }
                }

                if (contentToSearch.ToLowerInvariant().Contains(lowerQuery))
                {
                    results.Add(new ChatMessage
                    {
                        Id = m.Id,
                        ChannelId = m.ChannelId,
                        SenderId = m.SenderId,
                        SentAt = m.SentAt,
                        Content = contentToSearch,
                        IsEncrypted = false
                    });
                    if (results.Count >= 100) break;
                }
            }
        }

        return results;
    }
}



