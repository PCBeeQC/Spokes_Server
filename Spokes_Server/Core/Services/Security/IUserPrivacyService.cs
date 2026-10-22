using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Security;

public enum AddContactStatus
{
    Success = 0,
    NotFound = 1,
    AlreadyConnected = 2,
    RateLimited = 3,
    SelfContactDisallowed = 4,
    DiscoveryDisabled = 5
}

public record AddContactResult(AddContactStatus Status, Employee? Contact = null, string? Message = null);

public interface IUserPrivacyService
{
    PrivacyConfig GetPrivacyConfig();
    bool CanViewDirectory(Employee? caller);
    bool CanDiscoverUser(Employee? caller, Employee target);
    List<Employee> FilterDiscoverableEmployees(Employee? caller, IEnumerable<Employee> candidates);
    List<Team> FilterDiscoverableTeams(Employee? caller, IEnumerable<Team> teams);
    Task<IEnumerable<Employee>> SearchDiscoverableEmployeesAsync(
        Employee? caller,
        string query,
        IEnumerable<Employee>? candidatePool = null,
        int maxResults = 20);
    bool CanCreateCompanyWideEvents(Employee? caller);
    Task<AddContactResult> AddContactByIdentifierAsync(Employee caller, string identifier);
    Task<bool> RemoveContactAsync(Employee caller, string contactUserId);
    List<Employee> GetConnectedContacts(Employee caller);

    bool CanCreatePublicChannels(Employee? employee);
    bool CanAccessPublicChannels(Employee? employee);
    bool CanAccessPublicChannel(ChatChannel? channel, Employee? employee);
    bool ShouldHideMemberList(ChatChannel? channel, Employee? viewer);
    bool CanReactInChannel(ChatChannel? channel, Employee? employee);
    bool ShouldAnonymizeReactions(ChatChannel? channel, Employee? viewer);
    bool ShouldRestrictAlbumAudience(Album? album, Employee? viewer);
}
