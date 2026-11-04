namespace Spokes_Server.Core.Services.Communication;

using System;
using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Storage;

public class ChatFileAccessProvider : IFileAccessProvider
{
    private readonly IChatChannelAccessService _chatService;
    private readonly ChatChannelRepository? _channels;
    private readonly IChatAuthorizationService? _chatAuth;
    public string Category => "chat";

    public ChatFileAccessProvider(
        IChatChannelAccessService chatService,
        ChatChannelRepository? channels = null,
        IChatAuthorizationService? chatAuth = null)
    {
        _chatService = chatService;
        _channels = channels;
        _chatAuth = chatAuth;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user) || string.IsNullOrEmpty(contextId))
        {
            return false;
        }

        if (_chatAuth != null)
        {
            var channel = _channels?.GetById(contextId);
            if (channel != null)
            {
                return _chatAuth.CanUserAccessChannel(channel, user);
            }

            return _chatAuth.CanUserAccessChannel(contextId, user.Id);
        }

        var userChannels = _chatService.GetChannelsForUser(user.Id);
        return userChannels.Any(c => c.Id == contextId);
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user) || string.IsNullOrEmpty(contextId))
        {
            return false;
        }

        if (!CanAccess(user, contextId))
        {
            return false;
        }

        var channel = _channels?.GetById(contextId)
            ?? _chatService.GetChannelsForUser(user.Id).FirstOrDefault(c => c.Id == contextId);

        if (channel == null || channel.IsArchived)
        {
            return false;
        }

        if (_chatAuth != null)
        {
            return _chatAuth.CanUserPostToChannel(channel, user);
        }

        if (channel.IsAnnouncementOnly)
        {
            return user.IsAdmin
                || (channel.AllowedPostUserIds != null && channel.AllowedPostUserIds.Contains(user.Id))
                || (!string.IsNullOrEmpty(user.TeamId) && channel.AllowedPostTeamIds != null && channel.AllowedPostTeamIds.Contains(user.TeamId))
                || (channel.ChannelType == ChatChannelType.Group && channel.CreatedById == user.Id);
        }

        return true;
    }
}

public class AlbumFileAccessProvider : IFileAccessProvider
{
    private readonly AlbumRepository _albums;
    private readonly IChatChannelAccessService _chatAccess;
    public string Category => "albums";

    public AlbumFileAccessProvider(AlbumRepository albums, IChatChannelAccessService chatAccess)
    {
        _albums = albums;
        _chatAccess = chatAccess;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user) || string.IsNullOrEmpty(contextId)) return false;

        var album = _albums.GetById(contextId);
        if (album == null) return false;

        if (album.OwnerId == user.Id || album.ContributorUserIds.Contains(user.Id))
            return true;

        foreach (var channelId in album.SharedWithChannelIds)
        {
            var channelUsers = _chatAccess.GetUsersForChannel(channelId);
            if (channelUsers.Contains(user.Id))
                return true;
        }

        return false;
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user) || string.IsNullOrEmpty(contextId)) return false;

        var album = _albums.GetById(contextId);
        if (album == null) return false;

        return album.OwnerId == user.Id || album.ContributorUserIds.Contains(user.Id);
    }
}

public class EmailFileAccessProvider : IFileAccessProvider
{
    private readonly IEmailInboxService? _emailInboxService;
    private readonly EmailAccountRepository? _accounts;
    public string Category => "email";

    public EmailFileAccessProvider(IEmailInboxService? emailInboxService = null, EmailAccountRepository? accounts = null)
    {
        _emailInboxService = emailInboxService;
        _accounts = accounts;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user) || string.IsNullOrWhiteSpace(contextId))
        {
            return false;
        }

        string accountId = contextId;
        string? messageId = null;
        int sepIdx = contextId.IndexOf(':');
        if (sepIdx >= 0)
        {
            if (sepIdx == 0 || sepIdx >= contextId.Length - 1)
            {
                return false;
            }

            accountId = contextId[..sepIdx];
            messageId = contextId[(sepIdx + 1)..];
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }
        }

        if (accountId.Equals(CompanyBillInboxConstants.SystemEmployeeId, StringComparison.OrdinalIgnoreCase))
        {
            return user.IsAdmin
                || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Finance.AccessAccounting)
                || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Purchases.ViewBills)
                || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Purchases.ManageBills)
                || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Purchases.ApproveBills);
        }

        // All non-bill-inbox email attachment reads require explicit "accountId:messageId" message-level scoping
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        if (_emailInboxService != null)
        {
            return _emailInboxService.CanUserAccessEmailMessage(user, accountId, messageId);
        }

        var account = _accounts?.GetById(accountId);
        if (account != null && !account.IsServerManaged)
        {
            return string.Equals(account.OwnerEmployeeId, user.Id, StringComparison.OrdinalIgnoreCase)
                || account.CanUserAccess(user.Id);
        }

        return user.IsAdmin || user.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase);
    }

    public bool CanUpload(Employee user, string contextId) => false;
}
