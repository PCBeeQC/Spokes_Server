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
        if (IsAdmin(caller)) return true;

        var config = GetPrivacyConfig();
        return config.AllowDirectoryBrowsing;
    }

    public bool CanDiscoverUser(Employee? caller, Employee target)
    {
        if (target == null) return false;
        if (target.IsSystem || target.Id == AccountDeletionService.SystemDeletedUserId) return false;
        if (caller == null) return false;
        if (IsAdmin(caller)) return true;
        if (caller.Id == target.Id) return true;

        // Admin discoverability check
        if (IsDiscoverableAdmin(target)) return true;

        // Mutual connected contacts check
        if (caller.ContactUserIds.Contains(target.Id) || target.ContactUserIds.Contains(caller.Id))
            return true;

        var config = GetPrivacyConfig();
        switch (config.BaseScope)
        {
            case NetworkScope.All:
                return true;

            case NetworkScope.SameTeam:
                var callerTeams = GetUserTeamIds(caller);
                var targetTeams = GetUserTeamIds(target);
                return callerTeams.Overlaps(targetTeams);

            case NetworkScope.ContactsOnly:
            case NetworkScope.None:
            default:
                return false;
        }
    }

    public List<Employee> FilterDiscoverableEmployees(Employee? caller, IEnumerable<Employee> candidates)
    {
        if (caller == null) return [];
        if (IsAdmin(caller)) return candidates.Where(e => !e.IsSystem && e.Id != AccountDeletionService.SystemDeletedUserId).ToList();

        return candidates.Where(e => CanDiscoverUser(caller, e)).ToList();
    }

    public List<Team> FilterDiscoverableTeams(Employee? caller, IEnumerable<Team> teams)
    {
        if (caller == null) return [];
        if (IsAdmin(caller)) return teams.ToList();

        var config = GetPrivacyConfig();
        switch (config.BaseScope)
        {
            case NetworkScope.All:
                return teams.ToList();

            case NetworkScope.SameTeam:
                var callerTeams = GetUserTeamIds(caller);
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
        if (IsAdmin(caller)) return true;

        return GetPrivacyConfig().AllowNonAdminsCompanyWideCalendar;
    }

    public Task<AddContactResult> AddContactByIdentifierAsync(Employee caller, string identifier)
    {
        if (caller == null)
            throw new ArgumentNullException(nameof(caller));

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

        Employee? matched = null;

        if (config.DiscoveryMethod == UserDiscoveryMethod.ExactEmailOnly)
        {
            if (trimmed.Contains('@'))
            {
                matched = pool.FirstOrDefault(e => string.Equals(e.Email?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            }
        }
        else // ExactEmailOrUsername or OpenSearch
        {
            if (trimmed.Contains('@'))
            {
                matched = pool.FirstOrDefault(e => string.Equals(e.Email?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                matched = pool.FirstOrDefault(e =>
                    (!string.IsNullOrEmpty(e.Email) && string.Equals(e.Email.Split('@')[0].Trim(), trimmed, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(e.FullName?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (matched == null)
        {
            return Task.FromResult(new AddContactResult(AddContactStatus.NotFound, Message: "User not found."));
        }

        if (caller.ContactUserIds.Contains(matched.Id))
        {
            return Task.FromResult(new AddContactResult(AddContactStatus.AlreadyConnected, matched, $"{matched.FullName} is already in your contacts."));
        }

        // Mutual connection
        caller.ContactUserIds.Add(matched.Id);
        matched.ContactUserIds.Add(caller.Id);

        // Atomic persistence per Spokes rules
        _employees.Save(caller);
        _employees.Save(matched);

        return Task.FromResult(new AddContactResult(AddContactStatus.Success, matched, $"Added {matched.FullName} to your contacts."));
    }

    public Task<bool> RemoveContactAsync(Employee caller, string contactUserId)
    {
        if (caller == null || string.IsNullOrEmpty(contactUserId))
            return Task.FromResult(false);

        var removed = caller.ContactUserIds.Remove(contactUserId);
        if (removed)
        {
            _employees.Save(caller);
        }

        // Reciprocal removal
        var target = _employees.GetById(contactUserId);
        if (target != null && target.ContactUserIds.Remove(caller.Id))
        {
            _employees.Save(target);
        }

        return Task.FromResult(removed);
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

        var source = (candidatePool ?? _employees.GetActiveEmployees())
            .Where(e => e.IsActive && !e.IsSuspended && !e.IsBanned && !e.IsSystem && e.Id != AccountDeletionService.SystemDeletedUserId);

        var trimmedQuery = query?.Trim() ?? string.Empty;

        // Admin sees all candidates matching query
        if (IsAdmin(caller))
        {
            if (string.IsNullOrWhiteSpace(trimmedQuery))
            {
                return Task.FromResult(source.OrderBy(e => e.FullName).Take(maxResults).AsEnumerable());
            }

            return Task.FromResult(source
                .Where(e => MatchesQuery(e, trimmedQuery))
                .OrderBy(e => e.FullName)
                .Take(maxResults)
                .AsEnumerable());
        }

        var config = GetPrivacyConfig();

        // 1. Gather all currently discoverable users for this caller
        var currentlyDiscoverable = source.Where(e => CanDiscoverUser(caller, e));

        // 2. If out-of-band discovery by exact identifier is enabled, check if the query yields an exact match
        if (!string.IsNullOrWhiteSpace(trimmedQuery) &&
            config.DiscoveryMethod is UserDiscoveryMethod.ExactEmailOnly or UserDiscoveryMethod.ExactEmailOrUsername)
        {
            Employee? exactMatch = null;
            if (trimmedQuery.Contains('@'))
            {
                exactMatch = source.FirstOrDefault(e => string.Equals(e.Email?.Trim(), trimmedQuery, StringComparison.OrdinalIgnoreCase));
            }
            else if (config.DiscoveryMethod == UserDiscoveryMethod.ExactEmailOrUsername)
            {
                exactMatch = source.FirstOrDefault(e =>
                    (!string.IsNullOrEmpty(e.Email) && string.Equals(e.Email.Split('@')[0].Trim(), trimmedQuery, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(e.FullName?.Trim(), trimmedQuery, StringComparison.OrdinalIgnoreCase));
            }

            if (exactMatch != null && !CanDiscoverUser(caller, exactMatch))
            {
                // Return exact match along with any already discoverable matching users
                var combined = currentlyDiscoverable.Where(e => MatchesQuery(e, trimmedQuery)).ToList();
                combined.Insert(0, exactMatch);
                return Task.FromResult(combined.Take(maxResults).AsEnumerable());
            }
        }

        // 3. If in OpenSearch method and BaseScope is All, search across all
        if (config.DiscoveryMethod == UserDiscoveryMethod.OpenSearch && config.BaseScope == NetworkScope.All)
        {
            if (string.IsNullOrWhiteSpace(trimmedQuery))
            {
                return Task.FromResult(source.OrderBy(e => e.FullName).Take(maxResults).AsEnumerable());
            }
            return Task.FromResult(source
                .Where(e => MatchesQuery(e, trimmedQuery))
                .OrderBy(e => e.FullName)
                .Take(maxResults)
                .AsEnumerable());
        }

        // Standard scoped search
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return Task.FromResult(currentlyDiscoverable.OrderBy(e => e.FullName).Take(maxResults).AsEnumerable());
        }

        return Task.FromResult(currentlyDiscoverable
            .Where(e => MatchesQuery(e, trimmedQuery))
            .OrderBy(e => e.FullName)
            .Take(maxResults)
            .AsEnumerable());
    }

    public bool CanCreatePublicChannels(Employee? employee)
    {
        if (employee == null) return false;
        var config = GetPrivacyConfig();
        switch (config.PublicChannels)
        {
            case PublicChannelPolicy.Disabled:
                // Nobody (not even admins) can create public channels when disabled on the server
                return false;

            case PublicChannelPolicy.AnnouncementOnly:
                // Only admins can create public announcement broadcast channels
                return IsAdmin(employee);

            case PublicChannelPolicy.Allowed:
            default:
                return true;
        }
    }

    public bool CanAccessPublicChannels(Employee? employee)
    {
        if (employee == null) return false;
        var config = GetPrivacyConfig();
        return config.PublicChannels != PublicChannelPolicy.Disabled;
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
        if (IsAdmin(viewer)) return false;

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
        if (IsAdmin(employee)) return true;

        var config = GetPrivacyConfig();
        bool isAnnouncement = channel.IsAnnouncementOnly || 
            (channel.IsPublicChannel && config.PublicChannels == PublicChannelPolicy.AnnouncementOnly);

        if (isAnnouncement)
        {
            if (config.AnnouncementReactions == AnnouncementReactionMode.Disabled)
            {
                // Only allowed posters can react if reactions are disabled on announcements
                bool isAllowedPoster = (channel.AllowedPostUserIds != null && channel.AllowedPostUserIds.Contains(employee.Id)) ||
                                       (channel.AllowedPostTeamIds != null && channel.AllowedPostTeamIds.Any(t => GetUserTeamIds(employee).Contains(t)));
                return isAllowedPoster;
            }
        }

        return true;
    }

    public bool ShouldAnonymizeReactions(ChatChannel? channel, Employee? viewer)
    {
        if (channel == null) return false;
        if (IsAdmin(viewer)) return false;

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
        if (IsAdmin(viewer) || album.OwnerId == viewer.Id) return false;

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

    private HashSet<string> GetUserTeamIds(Employee? employee)
    {
        if (employee == null) return [];

        var teamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(employee.TeamId))
        {
            teamIds.Add(employee.TeamId);
        }

        var ledTeams = _teams.GetAll()
            .Where(t => t.LeaderId == employee.Id)
            .Select(t => t.Id);

        foreach (var id in ledTeams)
        {
            teamIds.Add(id);
        }

        return teamIds;
    }

    private static bool IsAdmin(Employee? caller) =>
        caller != null && (caller.IsAdmin || caller.HasPermission(AppPermissions.Admin.ManageUsers));

    private static bool MatchesQuery(Employee e, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return e.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (e.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (e.Position?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
