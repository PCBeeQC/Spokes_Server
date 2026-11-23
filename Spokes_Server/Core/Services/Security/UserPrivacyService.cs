using System.Collections.Concurrent;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Identity;

namespace Spokes_Server.Core.Services.Security;

public class UserPrivacyService : IUserPrivacyService
{
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;

    // Rate limiter tracking: callerId -> list of lookup timestamps within window
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _lookupTimestamps = new();
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(5);
    private const int MaxLookupsPerWindow = 10;

    public UserPrivacyService(
        CompanyProfileRepository companyProfiles,
        EmployeeRepository employees,
        TeamRepository teams)
    {
        _companyProfiles = companyProfiles ?? throw new ArgumentNullException(nameof(companyProfiles));
        _employees = employees ?? throw new ArgumentNullException(nameof(employees));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
    }

    public PrivacyConfig GetPrivacyConfig() =>
        _companyProfiles.Get()?.Privacy ?? new PrivacyConfig();

    public bool CanViewDirectory(Employee? caller)
    {
        if (caller == null) return false;
        var liveCaller = _employees.GetById(caller.Id) ?? caller;
        if (!liveCaller.IsActive || liveCaller.IsBanned || liveCaller.IsSystem) return false;
        if (IsAdmin(liveCaller) || IsUserManager(liveCaller)) return true;

        var config = GetPrivacyConfig();
        return config.AllowDirectoryBrowsing;
    }

    public bool CanDiscoverUser(Employee? caller, Employee target)
    {
        if (target == null) return false;
        if (target.IsSystem || target.Id == AccountDeletionService.SystemDeletedUserId) return false;
        if (caller == null) return false;
        var liveCaller = _employees.GetById(caller.Id) ?? caller;
        if (!liveCaller.IsActive || liveCaller.IsBanned || liveCaller.IsSystem) return false;
        if (IsAdmin(liveCaller)) return true;
        if (liveCaller.Id == target.Id) return true;

        // Admin discoverability check
        if (IsDiscoverableAdmin(target)) return true;

        // Mutual connected contacts check
        if (liveCaller.ContactUserIds.Contains(target.Id) || target.ContactUserIds.Contains(liveCaller.Id))
            return true;

        // Delegated user managers may browse non-admin directory members without bypassing admin stealth
        if (IsUserManager(liveCaller) && !target.IsAdmin)
            return true;

        var config = GetPrivacyConfig();
        switch (config.BaseScope)
        {
            case NetworkScope.All:
                return !target.IsAdmin || IsDiscoverableAdmin(target);

            case NetworkScope.SameTeam:
                var callerTeams = GetUserTeamIds(liveCaller);
                var targetTeams = GetUserTeamIds(target);
                return callerTeams.Overlaps(targetTeams);

            case NetworkScope.ContactsOnly:
            case NetworkScope.None:
            default:
                return false;
        }
    }

    public bool CanViewUserEmail(Employee? caller, Employee? target)
    {
        if (target == null || target.IsSystem || target.Id == AccountDeletionService.SystemDeletedUserId)
            return false;
        if (caller == null) return false;

        var liveCaller = _employees.GetById(caller.Id) ?? caller;
        if (!liveCaller.IsActive || liveCaller.IsBanned || liveCaller.IsSystem) return false;

        if (liveCaller.Id == target.Id) return true;
        if (IsAdmin(liveCaller)) return true;
        if (IsUserManager(liveCaller) && !target.IsAdmin) return true;

        return target.IsEmailPublic && CanDiscoverUser(liveCaller, target);
    }

    public List<Employee> FilterDiscoverableEmployees(Employee? caller, IEnumerable<Employee> candidates)
    {
        if (caller == null) return [];
        var liveCaller = _employees.GetById(caller.Id) ?? caller;
        if (!liveCaller.IsActive || liveCaller.IsBanned || liveCaller.IsSystem) return [];
        if (IsAdmin(liveCaller)) return candidates.Where(e => !e.IsSystem && e.Id != AccountDeletionService.SystemDeletedUserId).ToList();

        return candidates.Where(e => CanDiscoverUser(liveCaller, e)).ToList();
    }

    public List<Team> FilterDiscoverableTeams(Employee? caller, IEnumerable<Team> teams)
    {
        if (caller == null) return [];
        var liveCaller = _employees.GetById(caller.Id) ?? caller;
        if (!liveCaller.IsActive || liveCaller.IsBanned || liveCaller.IsSystem) return [];
        if (IsAdmin(liveCaller) || IsUserManager(liveCaller)) return teams.ToList();

        var config = GetPrivacyConfig();
        switch (config.BaseScope)
        {
            case NetworkScope.All:
                return teams.ToList();

            case NetworkScope.SameTeam:
                var callerTeams = GetUserTeamIds(liveCaller);
                return teams.Where(t => callerTeams.Contains(t.Id)).ToList();

            case NetworkScope.ContactsOnly:
            case NetworkScope.None:
            default:
                return [];
        }
    }

    public bool CanCreateCompanyWideEvents(Employee? caller)
    {
        if (caller == null) return false;
        var liveCaller = _employees.GetById(caller.Id) ?? caller;
        if (!liveCaller.IsActiveUnsuspended || liveCaller.IsSystem) return false;
        if (IsAdmin(liveCaller)) return true;

        return GetPrivacyConfig().AllowNonAdminsCompanyWideCalendar;
    }

    public Task<AddContactResult> AddContactByIdentifierAsync(Employee caller, string identifier)
    {
        if (caller == null)
            throw new ArgumentNullException(nameof(caller));

        var liveCaller = _employees.GetById(caller.Id);
        if (!caller.IsActiveUnsuspended || liveCaller is not { IsActiveUnsuspended: true })
        {
            return Task.FromResult(new AddContactResult(AddContactStatus.Forbidden, Message: "Suspended or inactive accounts cannot modify contacts."));
        }

        var config = GetPrivacyConfig();
        if (config.DiscoveryMethod == UserDiscoveryMethod.None)
        {
            return Task.FromResult(new AddContactResult(AddContactStatus.DiscoveryDisabled, Message: "Out-of-band user discovery is disabled."));
        }

        // Rate limit check
        if (IsRateLimited(caller.Id))
        {
            return Task.FromResult(new AddContactResult(AddContactStatus.RateLimited, Message: "Too many discovery attempts. Please wait a few minutes before trying again."));
        }

        var trimmed = identifier?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return Task.FromResult(new AddContactResult(AddContactStatus.NotFound, Message: "User not found."));
        }

        // Self-contact check
        if (string.Equals(caller.Email?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(caller.Email) && string.Equals(caller.Email.Split('@')[0].Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(new AddContactResult(AddContactStatus.SelfContactDisallowed, Message: "You cannot add yourself as a contact."));
        }

        var pool = _employees.GetActiveEmployees()
            .Where(e => e.IsActive && !e.IsSuspended && !e.IsBanned && !e.IsSystem && e.Id != AccountDeletionService.SystemDeletedUserId);

        var matched = FindExactEmployeeMatch(
            pool,
            trimmed,
            allowUsernameOrName: config.DiscoveryMethod != UserDiscoveryMethod.ExactEmailOnly);

        if (matched == null)
        {
            return Task.FromResult(new AddContactResult(AddContactStatus.NotFound, Message: "User not found."));
        }

        if (liveCaller.ContactUserIds.Contains(matched.Id))
        {
            return Task.FromResult(new AddContactResult(AddContactStatus.AlreadyConnected, matched, $"{matched.FullName} is already in your contacts."));
        }

        // Mutual connection
        liveCaller.ContactUserIds.Add(matched.Id);
        if (!ReferenceEquals(liveCaller, caller) && !caller.ContactUserIds.Contains(matched.Id))
        {
            caller.ContactUserIds.Add(matched.Id);
        }
        matched.ContactUserIds.Add(liveCaller.Id);

        // Atomic persistence per Spokes rules
        _employees.Save(liveCaller);
        _employees.Save(matched);

        return Task.FromResult(new AddContactResult(AddContactStatus.Success, matched, $"Added {matched.FullName} to your contacts."));
    }

    public Task<bool> RemoveContactAsync(Employee caller, string contactUserId)
    {
        if (caller is not { IsActiveUnsuspended: true } || string.IsNullOrEmpty(contactUserId))
            return Task.FromResult(false);

        var liveCaller = _employees.GetById(caller.Id);
        if (liveCaller is not { IsActiveUnsuspended: true })
            return Task.FromResult(false);

        var removed = liveCaller.ContactUserIds.Remove(contactUserId);
        if (!ReferenceEquals(liveCaller, caller))
        {
            caller.ContactUserIds.Remove(contactUserId);
        }
        if (removed)
        {
            _employees.Save(liveCaller);
        }

        // Reciprocal removal
        var target = _employees.GetById(contactUserId);
        if (target != null && target.ContactUserIds.Remove(liveCaller.Id))
        {
            _employees.Save(target);
        }

        return Task.FromResult(removed);
    }

    private static Employee? FindExactEmployeeMatch(IEnumerable<Employee> pool, string trimmedQuery, bool allowUsernameOrName)
    {
        if (trimmedQuery.Contains('@'))
        {
            return pool.FirstOrDefault(e =>
                string.Equals(e.Email?.Trim(), trimmedQuery, StringComparison.OrdinalIgnoreCase));
        }

        if (!allowUsernameOrName)
        {
            return null;
        }

        return pool.FirstOrDefault(e =>
            (!string.IsNullOrEmpty(e.Email) && string.Equals(e.Email.Split('@')[0].Trim(), trimmedQuery, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(e.FullName?.Trim(), trimmedQuery, StringComparison.OrdinalIgnoreCase));
    }

    public List<Employee> GetConnectedContacts(Employee caller)
    {
        if (caller == null || caller.ContactUserIds == null || caller.ContactUserIds.Count == 0)
            return [];

        return _employees.GetActiveEmployees()
            .Where(e => caller.ContactUserIds.Contains(e.Id) && e.IsActive && !e.IsSuspended && !e.IsBanned && !e.IsSystem)
            .OrderBy(e => e.FullName)
            .ToList();
    }

    public Task<IEnumerable<Employee>> SearchDiscoverableEmployeesAsync(
        Employee? caller,
        string query,
        IEnumerable<Employee>? candidatePool = null,
        int maxResults = 20)
    {
        if (caller == null)
        {
            return Task.FromResult(Enumerable.Empty<Employee>());
        }

        var liveCaller = _employees.GetById(caller.Id) ?? caller;
        if (!liveCaller.IsActive || liveCaller.IsBanned || liveCaller.IsSystem)
        {
            return Task.FromResult(Enumerable.Empty<Employee>());
        }

        var source = (candidatePool ?? _employees.GetActiveEmployees())
            .Where(e => e.IsActive && !e.IsSuspended && !e.IsBanned && !e.IsSystem && e.Id != AccountDeletionService.SystemDeletedUserId);

        var trimmedQuery = query?.Trim() ?? string.Empty;

        // Admin sees all candidates matching query
        if (IsAdmin(liveCaller))
        {
            if (string.IsNullOrWhiteSpace(trimmedQuery))
            {
                return Task.FromResult(source.OrderBy(e => e.FullName).Take(maxResults).AsEnumerable());
            }

            return Task.FromResult(source
                .Where(e => MatchesQuery(liveCaller, e, trimmedQuery))
                .OrderBy(e => e.FullName)
                .Take(maxResults)
                .AsEnumerable());
        }

        var config = GetPrivacyConfig();

        // 1. Gather all currently discoverable users for this caller
        var currentlyDiscoverable = source.Where(e => CanDiscoverUser(liveCaller, e));

        // 2. If out-of-band discovery by exact identifier is enabled, rate-limit before scanning non-discoverable users
        if (!string.IsNullOrWhiteSpace(trimmedQuery) &&
            liveCaller.IsActiveUnsuspended &&
            config.BaseScope != NetworkScope.All &&
            config.DiscoveryMethod is UserDiscoveryMethod.ExactEmailOnly or UserDiscoveryMethod.ExactEmailOrUsername)
        {
            bool canCheckOutOfBand = trimmedQuery.Contains('@') ||
                config.DiscoveryMethod == UserDiscoveryMethod.ExactEmailOrUsername;

            if (canCheckOutOfBand && !IsRateLimited(liveCaller.Id))
            {
                var outOfBandPool = source.Where(e => !e.IsAdmin || IsDiscoverableAdmin(e));
                var exactMatch = FindExactEmployeeMatch(
                    outOfBandPool,
                    trimmedQuery,
                    allowUsernameOrName: config.DiscoveryMethod == UserDiscoveryMethod.ExactEmailOrUsername);

                if (exactMatch != null && !CanDiscoverUser(liveCaller, exactMatch))
                {
                    // Return exact match along with any already discoverable matching users
                    var combined = currentlyDiscoverable.Where(e => MatchesQuery(liveCaller, e, trimmedQuery)).ToList();
                    combined.Insert(0, exactMatch);
                    return Task.FromResult(combined.Take(maxResults).AsEnumerable());
                }
            }
        }

        // Standard scoped search
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return Task.FromResult(currentlyDiscoverable.OrderBy(e => e.FullName).Take(maxResults).AsEnumerable());
        }

        return Task.FromResult(currentlyDiscoverable
            .Where(e => MatchesQuery(liveCaller, e, trimmedQuery))
            .OrderBy(e => e.FullName)
            .Take(maxResults)
            .AsEnumerable());
    }

    public bool CanCreatePublicChannels(Employee? employee)
    {
        if (employee == null) return false;
        var liveEmployee = _employees.GetById(employee.Id) ?? employee;
        if (!liveEmployee.IsActiveUnsuspended || liveEmployee.IsSystem) return false;
        var config = GetPrivacyConfig();
        switch (config.PublicChannels)
        {
            case PublicChannelPolicy.Disabled:
                // Nobody (not even admins) can create public channels when disabled on the server
                return false;

            case PublicChannelPolicy.AnnouncementOnly:
                // Only admins can create public announcement broadcast channels
                return IsAdmin(liveEmployee);

            case PublicChannelPolicy.Allowed:
            default:
                return true;
        }
    }

    public bool CanAccessPublicChannel(ChatChannel? channel, Employee? employee)
    {
        if (channel == null || employee == null) return false;

        if (!channel.IsPublicChannel)
        {
            return true;
        }

        var config = GetPrivacyConfig();
        switch (config.PublicChannels)
        {
            case PublicChannelPolicy.Disabled:
                return false;

            case PublicChannelPolicy.AnnouncementOnly:
                return channel.IsAnnouncementOnly;

            case PublicChannelPolicy.Allowed:
            default:
                return true;
        }
    }

    public bool ShouldHideMemberList(ChatChannel? channel, Employee? viewer)
    {
        if (channel == null) return false;
        var liveViewer = viewer == null ? null : (_employees.GetById(viewer.Id) ?? viewer);
        if (IsAdmin(liveViewer)) return false;

        // In Team, Project, and Group channels, the member list is NEVER hidden (teammates need to know who reads messages)
        if (channel.ChannelType == ChatChannelType.Team ||
            channel.ChannelType == ChatChannelType.Project ||
            channel.ChannelType == ChatChannelType.Group ||
            channel.ChannelType == ChatChannelType.Direct)
        {
            return false;
        }

        // For Public / Announcement channels:
        var config = GetPrivacyConfig();
        if (config.HidePublicChannelMemberList || config.PublicChannels != PublicChannelPolicy.Allowed)
        {
            return true;
        }

        return false;
    }

    public bool CanReactInChannel(ChatChannel? channel, Employee? employee)
    {
        if (channel == null || employee == null) return false;
        var liveEmployee = _employees.GetById(employee.Id) ?? employee;
        if (!liveEmployee.IsActiveUnsuspended || liveEmployee.IsSystem) return false;
        if (IsAdmin(liveEmployee)) return true;

        var config = GetPrivacyConfig();
        bool isAnnouncement = channel.IsAnnouncementOnly || 
            (channel.IsPublicChannel && config.PublicChannels == PublicChannelPolicy.AnnouncementOnly);

        if (isAnnouncement)
        {
            if (config.AnnouncementReactions == AnnouncementReactionMode.Disabled)
            {
                // Only allowed posters can react if reactions are disabled on announcements
                bool isAllowedPoster = (channel.AllowedPostUserIds != null && channel.AllowedPostUserIds.Contains(liveEmployee.Id)) ||
                                       (channel.AllowedPostTeamIds != null && channel.AllowedPostTeamIds.Any(t => GetUserTeamIds(liveEmployee).Contains(t)));
                return isAllowedPoster;
            }
        }

        return true;
    }

    public bool ShouldAnonymizeReactions(ChatChannel? channel, Employee? viewer)
    {
        if (channel == null) return false;
        var liveViewer = viewer == null ? null : (_employees.GetById(viewer.Id) ?? viewer);
        if (IsAdmin(liveViewer)) return false;

        var config = GetPrivacyConfig();
        bool isAnnouncement = channel.IsAnnouncementOnly || 
            (channel.IsPublicChannel && config.PublicChannels == PublicChannelPolicy.AnnouncementOnly);

        if (isAnnouncement)
        {
            return config.AnnouncementReactions == AnnouncementReactionMode.CountOnly;
        }

        return false;
    }

    public bool ShouldRestrictAlbumAudience(Album? album, Employee? viewer)
    {
        if (album == null || viewer == null) return false;
        var liveViewer = _employees.GetById(viewer.Id) ?? viewer;
        if (IsAdmin(liveViewer) || album.OwnerId == liveViewer.Id) return false;

        var config = GetPrivacyConfig();
        return config.RestrictAlbumAudienceVisibility || config.HidePublicChannelMemberList;
    }

    private bool IsDiscoverableAdmin(Employee target)
    {
        if (!target.IsAdmin) return false;

        var config = GetPrivacyConfig();
        return config.AdminVisibility switch
        {
            AdminVisibilityMode.AllAdmins => true,
            AdminVisibilityMode.DesignatedAdminsOnly => config.DesignatedAdminIds.Contains(target.Id),
            AdminVisibilityMode.None => false,
            _ => false
        };
    }

    private bool IsRateLimited(string callerId)
    {
        var now = DateTimeOffset.UtcNow;
        var timestamps = _lookupTimestamps.GetOrAdd(callerId, _ => new List<DateTimeOffset>());

        lock (timestamps)
        {
            timestamps.RemoveAll(t => now - t > RateLimitWindow);
            if (timestamps.Count >= MaxLookupsPerWindow)
            {
                return true;
            }
            timestamps.Add(now);
            return false;
        }
    }

    private HashSet<string> GetUserTeamIds(Employee? employee) =>
        TeamRepository.GetUserTeamIds(_teams, employee?.Id, employee);

    private static bool IsAdmin(Employee? caller) =>
        caller != null && !caller.IsSystem && caller.IsEffectiveAdmin;

    private static bool IsUserManager(Employee? caller) =>
        caller != null &&
        !caller.IsSystem &&
        caller.IsActiveUnsuspended &&
        !caller.IsAdmin &&
        (caller.HasPermission(AppPermissions.Admin.ManageUsers) ||
         caller.HasPermission(AppPermissions.Hr.ManageEmployees));

    private bool MatchesQuery(Employee? caller, Employee target, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (target.FullName.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (target.Position?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) return true;
        return CanViewUserEmail(caller, target) &&
               (target.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
