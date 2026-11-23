namespace Spokes_Server.Core.Services.Communication;

using System;
using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Storage;

public class ChatFileAccessProvider : IFileAccessProvider
{
    private readonly IChatAuthorizationService _chatAuth;
    private readonly ChatChannelRepository? _channels;
    public string Category => "chat";

    public ChatFileAccessProvider(
        IChatAuthorizationService chatAuth,
        ChatChannelRepository? channels = null)
    {
        _chatAuth = chatAuth ?? throw new ArgumentNullException(nameof(chatAuth));
        _channels = channels;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user) || string.IsNullOrEmpty(contextId))
        {
            return false;
        }

        if (!FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Chat.Use))
        {
            return false;
        }

        var channel = _channels?.GetById(contextId);
        if (channel != null && channel.IsArchived && !FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Chat.ViewArchive))
        {
            return false;
        }

        return channel != null
            ? _chatAuth.CanUserAccessChannel(channel, user)
            : _chatAuth.CanUserAccessChannel(contextId, user.Id);
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user) || string.IsNullOrEmpty(contextId))
        {
            return false;
        }

        if (!user.HasPermission(AppPermissions.Chat.Use))
        {
            return false;
        }

        if (!CanAccess(user, contextId))
        {
            return false;
        }

        var channel = _channels?.GetById(contextId)
            ?? _chatAuth.GetChannelsForUser(user.Id).FirstOrDefault(c => c.Id == contextId);

        if (channel == null || channel.IsArchived)
        {
            return false;
        }

        return _chatAuth.CanUserPostToChannel(channel, user);
    }
}

public class AlbumFileAccessProvider : IFileAccessProvider
{
    private readonly AlbumRepository _albums;
    private readonly IChatAuthorizationService _chatAuth;
    private readonly ChatChannelRepository? _channels;
    public string Category => "albums";

    public AlbumFileAccessProvider(
        AlbumRepository albums,
        IChatAuthorizationService chatAuth,
        ChatChannelRepository? channels = null)
    {
        _albums = albums;
        _chatAuth = chatAuth ?? throw new ArgumentNullException(nameof(chatAuth));
        _channels = channels;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user) || string.IsNullOrEmpty(contextId)) return false;

        if (!FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Albums.View)) return false;

        var album = _albums.GetById(contextId);
        if (album == null) return false;

        if (album.OwnerId == user.Id || album.ContributorUserIds.Contains(user.Id))
            return true;

        if (!FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Chat.Use))
            return false;

        foreach (var channelId in album.SharedWithChannelIds)
        {
            if (_channels != null)
            {
                var channel = _channels.GetById(channelId);
                if (channel == null)
                {
                    continue;
                }

                if (channel.IsArchived && !FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Chat.ViewArchive))
                {
                    continue;
                }

                if (_chatAuth.CanUserAccessChannel(channel, user))
                    return true;
            }
            else if (_chatAuth.CanUserAccessChannel(channelId, user.Id))
            {
                return true;
            }
        }

        return false;
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user) || string.IsNullOrEmpty(contextId)) return false;

        if (!user.HasPermission(AppPermissions.Albums.View)) return false;

        var album = _albums.GetById(contextId);
        if (album == null) return false;

        return album.OwnerId == user.Id || album.ContributorUserIds.Contains(user.Id);
    }
}

public class EmailFileAccessProvider : IFileAccessProvider
{
    private readonly IEmailInboxService _emailInboxService;
    private readonly CompanyProfileRepository? _companyProfile;
    public string Category => "email";

    public EmailFileAccessProvider(
        IEmailInboxService emailInboxService,
        CompanyProfileRepository? companyProfile = null)
    {
        _emailInboxService = emailInboxService ?? throw new ArgumentNullException(nameof(emailInboxService));
        _companyProfile = companyProfile;
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

        if (string.Equals(_companyProfile?.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // All non-bill-inbox email attachment reads require explicit "accountId:messageId" message-level scoping
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        return _emailInboxService.CanUserAccessEmailMessage(user, accountId, messageId);
    }

    public bool CanUpload(Employee user, string contextId) => false;
}
