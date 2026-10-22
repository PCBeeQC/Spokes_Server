namespace Spokes_Server.Core.Services.Communication;

using System;
using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Storage;

public class ChatFileAccessProvider : IFileAccessProvider
{
    private readonly IChatChannelAccessService _chatService;
    public string Category => "chat";

    public ChatFileAccessProvider(IChatChannelAccessService chatService)
    {
        _chatService = chatService;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned || string.IsNullOrEmpty(contextId))
        {
            return false;
        }

        var userChannels = _chatService.GetChannelsForUser(user.Id);
        return userChannels.Any(c => c.Id == contextId);
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
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;

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
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;

        var album = _albums.GetById(contextId);
        if (album == null) return false;

        return album.OwnerId == user.Id || album.ContributorUserIds.Contains(user.Id);
    }
}

public class EmailFileAccessProvider : IFileAccessProvider
{
    public string Category => "email";

    public bool CanAccess(Employee user, string contextId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned || string.IsNullOrWhiteSpace(contextId))
        {
            return false;
        }

        if (contextId.Equals(CompanyBillInboxConstants.SystemEmployeeId, StringComparison.OrdinalIgnoreCase))
        {
            return user.IsAdmin
                || user.HasPermission(AppPermissions.Finance.AccessAccounting)
                || user.HasPermission(AppPermissions.Purchases.ViewBills)
                || user.HasPermission(AppPermissions.Purchases.ManageBills)
                || user.HasPermission(AppPermissions.Purchases.ApproveBills);
        }

        return user.IsAdmin || user.Id.Equals(contextId, StringComparison.OrdinalIgnoreCase);
    }

    public bool CanUpload(Employee user, string contextId) => CanAccess(user, contextId);
}
