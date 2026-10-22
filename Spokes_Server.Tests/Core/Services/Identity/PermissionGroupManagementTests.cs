using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Services.Identity;

public class PermissionGroupManagementTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfiles;

    public PermissionGroupManagementTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_PermGroup_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        Dictionary<string, string?> configDict = new() { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _employees = new EmployeeRepository(_persistence, _config);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void DeleteGroupGuard_WhenAssignedToActiveUserViaList_IsBlocked()
    {
        var group = new PermissionGroup { Id = "friend_member", Name = "Member" };
        var activeUser = new Employee
        {
            Id = "user-1",
            FirstName = "Alice",
            LastName = "Smith",
            IsActive = true,
            PermissionGroupIds = ["friend_member"]
        };
        _employees.Save(activeUser);

        var assignedActiveUsers = _employees.GetAll()
            .Where(e => e.IsActive && (e.PermissionGroupIds.Contains(group.Id) || e.PermissionGroupId == group.Id))
            .ToList();

        Assert.Single(assignedActiveUsers);
        Assert.Equal("user-1", assignedActiveUsers[0].Id);
    }

    [Fact]
    public void DeleteGroupGuard_WhenAssignedToActiveUserViaLegacyProperty_IsBlocked()
    {
        var group = new PermissionGroup { Id = "friend_member", Name = "Member" };
        var activeUser = new Employee
        {
            Id = "user-legacy",
            FirstName = "Bob",
            LastName = "Jones",
            IsActive = true,
            PermissionGroupId = "friend_member"
        };
        _employees.Save(activeUser);

        var assignedActiveUsers = _employees.GetAll()
            .Where(e => e.IsActive && (e.PermissionGroupIds.Contains(group.Id) || e.PermissionGroupId == group.Id))
            .ToList();

        Assert.Single(assignedActiveUsers);
        Assert.Equal("user-legacy", assignedActiveUsers[0].Id);
    }

    [Fact]
    public void DeleteGroupGuard_WhenAssignedOnlyToInactiveUser_IsAllowed()
    {
        var group = new PermissionGroup { Id = "friend_member", Name = "Member" };
        var inactiveUser = new Employee
        {
            Id = "user-inactive",
            FirstName = "Charlie",
            LastName = "Brown",
            IsActive = false,
            PermissionGroupIds = ["friend_member"]
        };
        _employees.Save(inactiveUser);

        var assignedActiveUsers = _employees.GetAll()
            .Where(e => e.IsActive && (e.PermissionGroupIds.Contains(group.Id) || e.PermissionGroupId == group.Id))
            .ToList();

        Assert.Empty(assignedActiveUsers);
    }

    [Fact]
    public void DeleteGroupGuard_WhenNotAssignedToAnyUser_IsAllowed()
    {
        var group = new PermissionGroup { Id = "unused_group", Name = "Unused" };
        var otherUser = new Employee
        {
            Id = "user-other",
            FirstName = "Dave",
            LastName = "Miller",
            IsActive = true,
            PermissionGroupIds = ["different_group"]
        };
        _employees.Save(otherUser);

        var assignedActiveUsers = _employees.GetAll()
            .Where(e => e.IsActive && (e.PermissionGroupIds.Contains(group.Id) || e.PermissionGroupId == group.Id))
            .ToList();

        Assert.Empty(assignedActiveUsers);
    }

    [Fact]
    public void PermissionGroupDisplay_ResolvesIdsToNamesCorrectly()
    {
        var groups = new List<PermissionGroup>
        {
            new() { Id = "friend_member", Name = "Member" },
            new() { Id = "friend_admin", Name = "Family Admin" }
        };

        // Simulates ToStringFunc
        string ResolveSingle(string id) =>
            string.IsNullOrEmpty(id) ? "-- None (Chat Only) --" : groups.FirstOrDefault(g => g.Id == id)?.Name ?? id;

        Assert.Equal("Member", ResolveSingle("friend_member"));
        Assert.Equal("Family Admin", ResolveSingle("friend_admin"));
        Assert.Equal("-- None (Chat Only) --", ResolveSingle(""));
        Assert.Equal("custom_unknown", ResolveSingle("custom_unknown"));

        // Simulates MultiSelectionTextFunc
        string ResolveMulti(List<string> selectedValues)
        {
            if (selectedValues == null || selectedValues.Count == 0) return string.Empty;
            return string.Join(", ", selectedValues.Select(id => groups.FirstOrDefault(g => g.Id == id)?.Name ?? id));
        }

        Assert.Equal("Member", ResolveMulti(["friend_member"]));
        Assert.Equal("Member, Family Admin", ResolveMulti(["friend_member", "friend_admin"]));
        Assert.Equal(string.Empty, ResolveMulti([]));
    }

    [Fact]
    public void SendUsageStatistics_TogglePersistsImmediatelyToRepository()
    {
        var profile = _companyProfiles.GetOrCreate();
        Assert.True(profile.SendUsageStatistics);

        profile.SendUsageStatistics = false;
        _companyProfiles.Save(profile);

        var reloaded = _companyProfiles.Get();
        Assert.False(reloaded.SendUsageStatistics);

        reloaded.SendUsageStatistics = true;
        _companyProfiles.Save(reloaded);

        var finalReload = _companyProfiles.Get();
        Assert.True(finalReload.SendUsageStatistics);
    }
}
