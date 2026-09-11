using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
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
using Spokes_Server.Core.Constants;

namespace Spokes_Server.Core.Services.Core;

internal static class ProjectAccessHelper
{
    public static bool HasAccess(ProjectRepository projects, TeamRepository teams, Employee user, string projectId)
    {
        if (user.IsAdmin) return true;

        var project = projects.GetById(projectId);
        if (project == null) return false;

        if (project.AccessPolicy == "Public") return true;

        bool isAllowed = project.AllowedUserIds.Contains(user.Id);
        if (!isAllowed && !string.IsNullOrEmpty(user.TeamId))
        {
            isAllowed = project.AllowedTeamIds.Contains(user.TeamId);
        }

        // Check if user is a team leader of an allowed team
        if (!isAllowed)
        {
            isAllowed = teams.GetAll().Any(t => t.LeaderId == user.Id && project.AllowedTeamIds.Contains(t.Id));
        }

        return isAllowed;
    }
}

public interface IFileAccessProvider
{
    string Category { get; }
    Task<bool> CanAccessAsync(Employee user, string contextId);
}

public class ProjectFileAccessProvider : IFileAccessProvider
{
    private readonly ProjectRepository _projects;
    private readonly TeamRepository _teams;
    public string Category => "projects";

    public ProjectFileAccessProvider(ProjectRepository projects, TeamRepository teams)
    {
        _projects = projects;
        _teams = teams;
    }

    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        return Task.FromResult(ProjectAccessHelper.HasAccess(_projects, _teams, user, contextId));
    }
}

public class ExpenseFileAccessProvider : IFileAccessProvider
{
    private readonly ExpenseReportRepository _expenseReports;
    public string Category => "expenses";

    public ExpenseFileAccessProvider(ExpenseReportRepository expenseReports)
    {
        _expenseReports = expenseReports;
    }

    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        // Finance admins can see all expenses
        if (user.IsAdmin || user.HasPermission(AppPermissions.Finance.AccessAccounting)) return Task.FromResult(true);

        var report = _expenseReports.GetById(contextId);
        if (report == null) return Task.FromResult(false);

        return Task.FromResult(report.EmployeeId == user.Id);
    }
}

public class BillFileAccessProvider : IFileAccessProvider
{
    public string Category => "bills";

    // Bills are strictly for finance/admin
    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        if (user.IsAdmin || user.HasPermission(AppPermissions.Finance.AccessAccounting)) return Task.FromResult(true);
        return Task.FromResult(false);
    }
}

public class ChatFileAccessProvider : IFileAccessProvider
{
    private readonly IChatChannelAccessService _chatService;
    public string Category => "chat";

    public ChatFileAccessProvider(IChatChannelAccessService chatService)
    {
        _chatService = chatService;
    }

    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        if (user.IsAdmin) return Task.FromResult(true);

        var userChannels = _chatService.GetChannelsForUser(user.Id);
        return Task.FromResult(userChannels.Any(c => c.Id == contextId));
    }
}

public class RecurringCostFileAccessProvider : IFileAccessProvider
{
    public string Category => "recurringcosts";

    // Recurring costs/one-time expenses are finance-only
    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        if (user.IsAdmin || user.HasPermission(AppPermissions.Finance.AccessAccounting)) return Task.FromResult(true);
        return Task.FromResult(false);
    }
}

public class ProjectNoteFileAccessProvider : IFileAccessProvider
{
    private readonly ProjectRepository _projects;
    private readonly TeamRepository _teams;
    public string Category => "projectnotes";

    public ProjectNoteFileAccessProvider(ProjectRepository projects, TeamRepository teams)
    {
        _projects = projects;
        _teams = teams;
    }

    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        return Task.FromResult(ProjectAccessHelper.HasAccess(_projects, _teams, user, contextId));
    }
}

public class AvatarFileAccessProvider : IFileAccessProvider
{
    public string Category => "avatars";

    // Avatars are public to all authenticated users
    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        return Task.FromResult(true);
    }
}

public class SignatureFileAccessProvider : IFileAccessProvider
{
    public string Category => "signatures";

    // Signatures. We just align with the private use requirement
    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        if (user.IsAdmin || user.Id == contextId) return Task.FromResult(true);
        // Allow if user has finance/accounting permissions for generating documents
        return Task.FromResult(user.HasPermission(AppPermissions.Finance.AccessAccounting));
    }
}

public class AlbumFileAccessProvider : IFileAccessProvider
{
    private readonly AlbumRepository _albums;
    private readonly Spokes_Server.Core.Services.Communication.Chat.IChatChannelAccessService _chatAccess;
    public string Category => "albums";

    public AlbumFileAccessProvider(AlbumRepository albums, Spokes_Server.Core.Services.Communication.Chat.IChatChannelAccessService chatAccess)
    {
        _albums = albums;
        _chatAccess = chatAccess;
    }

    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        var album = _albums.GetById(contextId);
        if (album == null) return Task.FromResult(false);

        if (album.OwnerId == user.Id || album.ContributorUserIds.Contains(user.Id))
            return Task.FromResult(true);

        foreach (var channelId in album.SharedWithChannelIds)
        {
            var channelUsers = _chatAccess.GetUsersForChannel(channelId);
            if (channelUsers.Contains(user.Id))
                return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }
}

public class TempFileAccessProvider : IFileAccessProvider
{
    public string Category => "temp";

    public Task<bool> CanAccessAsync(Employee user, string contextId)
    {
        // Any active, non-suspended, non-banned authenticated employee can use temp staging
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}
