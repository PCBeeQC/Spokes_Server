using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Projects;

public class ProjectAccessHelperTests : TestDataTestBase
{
    private readonly DiskPersistenceService _persistenceService;
    private readonly IConfiguration _config;
    private readonly ProjectRepository _projectRepo;
    private readonly TeamRepository _teamRepo;

    public ProjectAccessHelperTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        _config = mockConfig.Object;

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistenceService = new DiskPersistenceService(mockLogger.Object);

        _projectRepo = new ProjectRepository(_persistenceService, _config);
        _teamRepo = new TeamRepository(_persistenceService, _config);
    }

    private static Employee CreateStandardUser(string? id = null, string? teamId = null, bool hasViewPermission = true)
    {
        var user = new Employee
        {
            Id = id ?? Guid.NewGuid().ToString(),
            IsActive = true,
            IsSuspended = false,
            IsBanned = false,
            IsAdmin = false,
            TeamId = teamId ?? string.Empty
        };

        if (hasViewPermission)
        {
            user.Permissions.Add(AppPermissions.Projects.View);
        }

        return user;
    }

    private static Project CreateRestrictedProject(string? id = null)
    {
        return new Project
        {
            Id = id ?? Guid.NewGuid().ToString(),
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = [],
            AllowedTeamIds = [],
            ProjectManagerId = string.Empty
        };
    }

    [Fact]
    public void CanAccess_ReturnsFalse_WhenUserIsNull()
    {
        var project = new Project { AccessPolicy = ProjectAccessPolicy.Public };
        Assert.False(ProjectAccessHelper.CanAccess(null, project));
    }

    [Fact]
    public void CanAccess_ReturnsFalse_WhenProjectIsNull()
    {
        var user = CreateStandardUser();
        Assert.False(ProjectAccessHelper.CanAccess(user, null));
    }

    [Theory]
    [InlineData(false, false, false)] // Inactive
    [InlineData(true, true, false)]  // Suspended
    [InlineData(true, false, true)]  // Banned
    public void CanAccess_ReturnsFalse_WhenUserStatusIsInvalid(bool isActive, bool isSuspended, bool isBanned)
    {
        var user = new Employee
        {
            IsActive = isActive,
            IsSuspended = isSuspended,
            IsBanned = isBanned,
            IsAdmin = true
        };
        var project = new Project { AccessPolicy = ProjectAccessPolicy.Public };

        Assert.False(ProjectAccessHelper.CanAccess(user, project));
    }

    [Fact]
    public void CanAccess_AdminUser_ReturnsTrue_Unconditionally()
    {
        var admin = new Employee
        {
            IsActive = true,
            IsAdmin = true
        };
        // Admin has no permissions, project is restricted and empty
        var project = CreateRestrictedProject();

        Assert.True(ProjectAccessHelper.CanAccess(admin, project));
    }

    [Fact]
    public void CanAccess_ReturnsFalse_WhenNonAdminUserLacksProjectsViewPermission()
    {
        var user = CreateStandardUser(hasViewPermission: false);
        user.Id = "user-1";

        var publicProject = new Project { AccessPolicy = ProjectAccessPolicy.Public };
        var assignedProject = new Project
        {
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = ["user-1"],
            ProjectManagerId = "user-1"
        };

        Assert.False(ProjectAccessHelper.CanAccess(user, publicProject));
        Assert.False(ProjectAccessHelper.CanAccess(user, assignedProject));
    }

    [Fact]
    public void CanAccess_ReturnsTrue_WhenProjectAccessPolicyIsPublic()
    {
        var user = CreateStandardUser();
        var project = new Project { AccessPolicy = ProjectAccessPolicy.Public };

        Assert.True(ProjectAccessHelper.CanAccess(user, project));
    }

    [Fact]
    public void CanAccess_ReturnsTrue_WhenUserIsDirectlyInAllowedUserIds()
    {
        var user = CreateStandardUser("user-42");
        var project = CreateRestrictedProject();
        project.AllowedUserIds = ["user-42", "user-99"];

        Assert.True(ProjectAccessHelper.CanAccess(user, project));
    }

    [Fact]
    public void CanAccess_ReturnsTrue_WhenUserIsDesignatedProjectManager()
    {
        var user = CreateStandardUser("pm-user");
        var project = CreateRestrictedProject();
        project.ProjectManagerId = "pm-user";

        Assert.True(ProjectAccessHelper.CanAccess(user, project));
    }

    [Fact]
    public void CanAccess_ReturnsTrue_WhenUserPrimaryTeamIsInAllowedTeamIds()
    {
        var user = CreateStandardUser("user-1", teamId: "team-alpha");
        var project = CreateRestrictedProject();
        project.AllowedTeamIds = ["team-alpha", "team-beta"];

        Assert.True(ProjectAccessHelper.CanAccess(user, project));
    }

    [Fact]
    public void CanAccess_ReturnsTrue_WhenUserIsLeaderOfAllowedTeam()
    {
        var user = CreateStandardUser("leader-1", teamId: "team-other");
        var project = CreateRestrictedProject();
        project.AllowedTeamIds = ["team-managed"];

        var team = new Team
        {
            Id = "team-managed",
            Name = "Managed Team",
            LeaderId = "leader-1"
        };
        _teamRepo.Save(team);

        Assert.True(ProjectAccessHelper.CanAccess(user, project, _teamRepo));
    }

    [Fact]
    public void CanAccess_ReturnsFalse_WhenUserIsLeaderOfDifferentTeamNotAllowed()
    {
        var user = CreateStandardUser("leader-1", teamId: "team-other");
        var project = CreateRestrictedProject();
        project.AllowedTeamIds = ["team-allowed"];

        var team = new Team
        {
            Id = "team-not-allowed",
            Name = "Different Team",
            LeaderId = "leader-1"
        };
        _teamRepo.Save(team);

        Assert.False(ProjectAccessHelper.CanAccess(user, project, _teamRepo));
    }

    [Fact]
    public void CanAccess_ReturnsFalse_WhenTeamsRepositoryIsNull_AndUserOnlyQualifiesAsLeader()
    {
        var user = CreateStandardUser("leader-1", teamId: "team-other");
        var project = CreateRestrictedProject();
        project.AllowedTeamIds = ["team-managed"];

        // Without team repository, leadership cannot be resolved
        Assert.False(ProjectAccessHelper.CanAccess(user, project, teams: null));
    }

    [Fact]
    public void CanAccess_ReturnsFalse_WhenRestrictedAndNoBranchesMatch()
    {
        var user = CreateStandardUser("stranger", teamId: "team-unknown");
        var project = CreateRestrictedProject();
        project.AllowedUserIds = ["someone-else"];
        project.AllowedTeamIds = ["team-allowed"];
        project.ProjectManagerId = "pm-other";

        Assert.False(ProjectAccessHelper.CanAccess(user, project, _teamRepo));
    }

    [Fact]
    public void CanAccessWithTeamIds_BoundaryAndPermissionChecks()
    {
        var user = CreateStandardUser(hasViewPermission: false);
        var project = CreateRestrictedProject();
        project.AllowedTeamIds = ["team-1"];

        // Null user / project
        Assert.False(ProjectAccessHelper.CanAccess(null, project, ["team-1"]));
        Assert.False(ProjectAccessHelper.CanAccess(user, null, ["team-1"]));

        // Lacks view permission
        Assert.False(ProjectAccessHelper.CanAccess(user, project, ["team-1"]));

        // Admin override
        user.IsAdmin = true;
        Assert.True(ProjectAccessHelper.CanAccess(user, project, ["team-1"]));
    }

    [Fact]
    public void CanAccessWithTeamIds_ResolvesAccessByResolvedTeamIds()
    {
        var user = CreateStandardUser("user-1", teamId: "primary-team");
        var project = CreateRestrictedProject();
        project.AllowedTeamIds = ["resolved-team"];

        // User belongs to resolved-team via pre-resolved list
        Assert.True(ProjectAccessHelper.CanAccess(user, project, ["resolved-team"]));

        // Resolved teams do not match
        Assert.False(ProjectAccessHelper.CanAccess(user, project, ["unrelated-team"]));
    }

    [Fact]
    public void CanAccessWithTeamIds_FallsBackToPrimaryTeamId_WhenPreResolvedListIsNull()
    {
        var user = CreateStandardUser("user-1", teamId: "fallback-team");
        var project = CreateRestrictedProject();
        project.AllowedTeamIds = ["fallback-team"];

        // null list falls back to user.TeamId
        Assert.True(ProjectAccessHelper.CanAccess(user, project, (IEnumerable<string>?)null));

        // When primary team does not match
        user.TeamId = "other-team";
        Assert.False(ProjectAccessHelper.CanAccess(user, project, (IEnumerable<string>?)null));
    }

    [Fact]
    public void CanAccessWithTeamIds_ReturnsFalse_WhenPreResolvedListIsEmpty_EvenIfPrimaryTeamMatches()
    {
        var user = CreateStandardUser("user-1", teamId: "fallback-team");
        var project = CreateRestrictedProject();
        project.AllowedTeamIds = ["fallback-team"];

        // An explicitly provided empty list means no teams were pre-resolved, so it does not fall back
        Assert.False(ProjectAccessHelper.CanAccess(user, project, new List<string>()));
    }

    [Fact]
    public void HasAccess_ReturnsFalse_WhenUserStatusIsInvalid()
    {
        var inactiveUser = new Employee { IsActive = false };
        Assert.False(ProjectAccessHelper.HasAccess(_projectRepo, _teamRepo, inactiveUser, "proj-1"));

        var suspendedUser = new Employee { IsActive = true, IsSuspended = true };
        Assert.False(ProjectAccessHelper.HasAccess(_projectRepo, _teamRepo, suspendedUser, "proj-1"));

        var bannedUser = new Employee { IsActive = true, IsBanned = true };
        Assert.False(ProjectAccessHelper.HasAccess(_projectRepo, _teamRepo, bannedUser, "proj-1"));
    }

    [Fact]
    public void HasAccess_ReturnsTrue_ForAdmin_EvenIfProjectDoesNotExist()
    {
        var admin = new Employee { IsActive = true, IsAdmin = true };
        Assert.True(ProjectAccessHelper.HasAccess(_projectRepo, _teamRepo, admin, "non-existent-proj"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasAccess_ReturnsFalse_WhenProjectIdIsNullOrWhitespace(string? projectId)
    {
        var user = CreateStandardUser();
        Assert.False(ProjectAccessHelper.HasAccess(_projectRepo, _teamRepo, user, projectId!));
    }

    [Fact]
    public void HasAccess_ReturnsFalse_WhenProjectNotFoundInRepository()
    {
        var user = CreateStandardUser();
        Assert.False(ProjectAccessHelper.HasAccess(_projectRepo, _teamRepo, user, "missing-project-id"));
    }

    [Fact]
    public void HasAccess_ReturnsTrue_WhenProjectExistsAndUserHasAccess()
    {
        var user = CreateStandardUser("user-1");
        var project = new Project
        {
            Id = "proj-accessible",
            AccessPolicy = ProjectAccessPolicy.Public
        };
        _projectRepo.Save(project);

        Assert.True(ProjectAccessHelper.HasAccess(_projectRepo, _teamRepo, user, "proj-accessible"));
    }

    [Fact]
    public void HasAccess_ReturnsFalse_WhenProjectExistsAndUserLacksAccess()
    {
        var user = CreateStandardUser("user-1");
        var project = new Project
        {
            Id = "proj-restricted",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = ["someone-else"]
        };
        _projectRepo.Save(project);

        Assert.False(ProjectAccessHelper.HasAccess(_projectRepo, _teamRepo, user, "proj-restricted"));
    }

    [Fact]
    public void CanAccess_SystemRendererWithProjectsView_ReturnsTrueForRestrictedProjects()
    {
        var renderer = new Employee
        {
            Id = Spokes_Server.Core.Middleware.RenderTokenMiddleware.SystemRendererId,
            IsActive = true,
            IsAdmin = false,
            IsSystem = true,
            Permissions = [AppPermissions.Projects.View]
        };
        var restrictedProject = CreateRestrictedProject("proj-restricted-pdf");
        _projectRepo.Save(restrictedProject);

        Assert.True(ProjectAccessHelper.CanAccess(renderer, restrictedProject, _teamRepo));
        Assert.True(ProjectAccessHelper.CanAccess(renderer, restrictedProject, Array.Empty<string>()));
        Assert.True(ProjectAccessHelper.HasAccess(_projectRepo, _teamRepo, renderer, "proj-restricted-pdf"));
    }

    [Fact]
    public void CanAccess_SystemRendererWithoutProjectsView_ReturnsFalse()
    {
        var renderer = new Employee
        {
            Id = Spokes_Server.Core.Middleware.RenderTokenMiddleware.SystemRendererId,
            IsActive = true,
            IsAdmin = false,
            IsSystem = true,
            Permissions = []
        };
        var restrictedProject = CreateRestrictedProject();

        Assert.False(ProjectAccessHelper.CanAccess(renderer, restrictedProject, _teamRepo));
        Assert.False(ProjectAccessHelper.CanAccess(renderer, restrictedProject, Array.Empty<string>()));
    }
}
