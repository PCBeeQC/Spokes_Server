namespace Spokes_Server.Tests.Core.Security;

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;
using Spokes_Server.Tests.Helpers;

public class PermissionAuthorizationHandlerTests
{
    private readonly Mock<EmployeeRepository> _mockEmployeeRepo;
    private readonly Database _db;
    private readonly PermissionAuthorizationHandler _handler;

    public PermissionAuthorizationHandlerTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns("Data");

        _mockEmployeeRepo = new Mock<EmployeeRepository>(null!, mockConfig.Object);
        _db = TestDatabaseFactory.Create(null, _mockEmployeeRepo.Object);
        _handler = new PermissionAuthorizationHandler(_mockEmployeeRepo.Object);
    }

    private static AuthorizationHandlerContext CreateContext(
        PermissionRequirement requirement,
        IEnumerable<Claim>? claims = null)
    {
        var identity = claims != null
            ? new ClaimsIdentity(claims, "TestAuth")
            : new ClaimsIdentity();
        var principal = new ClaimsPrincipal(identity);
        return new AuthorizationHandlerContext(new[] { requirement }, principal, null);
    }

    [Fact]
    public void PermissionRequirement_Constructor_SetsPermissionProperty()
    {
        var requirement = new PermissionRequirement("Projects.View");

        Assert.Equal("Projects.View", requirement.Permission);
    }

    #region Fail-closed when EmployeeId claim is missing or whitespace

    [Fact]
    public async Task HandleRequirementAsync_MissingEmployeeId_UserHasRoleAdmin_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim(ClaimTypes.Role, "Admin")
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_MissingEmployeeId_UserHasMatchingPermissionClaim_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("Permission", AppPermissions.Projects.View)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleRequirementAsync_WhitespaceEmployeeId_UserHasAdminAndPermissionClaims_DoesNotSucceed(string employeeId)
    {
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("Permission", AppPermissions.Projects.View)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_MissingEmployeeId_UserLacksRoleAndMatchingClaim_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("Permission", AppPermissions.Chat.Use)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_MissingEmployeeId_AnonymousUser_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var context = CreateContext(requirement, null);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    #endregion

    #region Scoped claim evaluation when EmployeeId claim is system-renderer

    [Fact]
    public async Task HandleRequirementAsync_SystemRenderer_UserHasRoleAdmin_Succeeds()
    {
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_SystemRenderer_UserHasMatchingPermissionClaim_Succeeds()
    {
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim("Permission", AppPermissions.Projects.View)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_SystemRenderer_UserLacksRoleAndMatchingClaim_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim("Permission", AppPermissions.Chat.Use)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_SystemRenderer_AdminRequirementWithoutAdminRole_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AppPermissions.Admin.RoleName);
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim("Permission", AppPermissions.Projects.View)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    #endregion

    #region Valid EmployeeId claim

    [Fact]
    public async Task HandleRequirementAsync_ValidEmployeeId_EmployeeNotFoundInDatabase_DoesNotSucceed()
    {
        const string employeeId = "emp-unknown";
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns((Employee?)null);

        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminRequirement_EmployeeIsAdmin_Succeeds()
    {
        const string employeeId = "emp-admin";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = true,
            IsActive = true
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Admin.RoleName);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminRequirement_EmployeeIsNotAdmin_DoesNotSucceed()
    {
        const string employeeId = "emp-nonadmin";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string> { AppPermissions.Projects.View }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Admin.RoleName);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminRequirement_EmployeeIsSuspended_DoesNotSucceed()
    {
        const string employeeId = "emp-admin-suspended";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = true,
            IsActive = true,
            IsSuspended = true
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Admin.RoleName);
        var claims = new[] { new Claim("EmployeeId", employeeId) };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminRequirement_EmployeeIsBanned_DoesNotSucceed()
    {
        const string employeeId = "emp-admin-banned";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = true,
            IsActive = true,
            IsBanned = true
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Admin.RoleName);
        var claims = new[] { new Claim("EmployeeId", employeeId) };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminRequirement_EmployeeIsInactive_DoesNotSucceed()
    {
        const string employeeId = "emp-admin-inactive";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = true,
            IsActive = false
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Admin.RoleName);
        var claims = new[] { new Claim("EmployeeId", employeeId) };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_RegularPermission_EmployeeHasPermission_Succeeds()
    {
        const string employeeId = "emp-worker";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string> { AppPermissions.Projects.View }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_RegularPermission_EmployeeLacksPermission_DoesNotSucceed()
    {
        const string employeeId = "emp-worker";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string> { AppPermissions.Projects.Create }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_RegularPermission_EmployeeIsAdmin_Succeeds()
    {
        const string employeeId = "emp-admin-regular";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = true,
            IsActive = true,
            Permissions = new List<string>()
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ValidEmployeeId_UserHasPermissionClaim_StillFailsIfEmployeeLacksPermissionInDb()
    {
        const string employeeId = "emp-worker";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string>()
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId),
            new Claim("Permission", AppPermissions.Projects.View)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ViewPOs_EmployeeHasManagePOs_Succeeds()
    {
        const string employeeId = "emp-po-mgr";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string> { AppPermissions.Purchases.ManagePOs }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[] { new Claim("EmployeeId", employeeId) };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ViewPOs_EmployeeHasManageBills_DoesNotSucceed()
    {
        const string employeeId = "emp-bill-mgr";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string> { AppPermissions.Purchases.ManageBills }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[] { new Claim("EmployeeId", employeeId) };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ViewPOs_EmployeeHasViewBills_DoesNotSucceed()
    {
        const string employeeId = "emp-bill-viewer";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string> { AppPermissions.Purchases.ViewBills }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[] { new Claim("EmployeeId", employeeId) };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ViewPOs_EmployeeHasApproveBills_DoesNotSucceed()
    {
        const string employeeId = "emp-bill-approver";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string> { AppPermissions.Purchases.ApproveBills }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[] { new Claim("EmployeeId", employeeId) };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ViewPOs_MissingEmployeeId_UserHasViewBillsClaim_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[]
        {
            new Claim("Permission", AppPermissions.Purchases.ViewBills)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ViewPOs_MissingEmployeeId_UserHasApproveBillsClaim_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[]
        {
            new Claim("Permission", AppPermissions.Purchases.ApproveBills)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ViewPOs_SystemRenderer_UserHasViewBillsClaim_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim("Permission", AppPermissions.Purchases.ViewBills)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ViewPOs_SystemRenderer_UserHasManagePOsClaim_Succeeds()
    {
        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim("Permission", AppPermissions.Purchases.ManagePOs)
        };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    #endregion

    #region Timesheets.ViewReport for Team Leaders

    [Fact]
    public async Task HandleRequirementAsync_TimesheetsViewReport_TeamLeader_Succeeds()
    {
        const string employeeId = "emp-leader";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string>()
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns("Data");
        var mockTeamRepo = new Mock<TeamRepository>(null!, mockConfig.Object);
        mockTeamRepo.Setup(r => r.GetAll()).Returns(new List<Team>
        {
            new Team { Id = "team-1", Name = "Engineering", LeaderId = employeeId }
        });

        var handler = new PermissionAuthorizationHandler(_mockEmployeeRepo.Object, mockTeamRepo.Object);

        var requirement = new PermissionRequirement(AppPermissions.Timesheets.ViewReport);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId)
        };
        var context = CreateContext(requirement, claims);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_TimesheetsViewReport_NotTeamLeaderAndNoPermission_DoesNotSucceed()
    {
        const string employeeId = "emp-member";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string>()
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns("Data");
        var mockTeamRepo = new Mock<TeamRepository>(null!, mockConfig.Object);
        mockTeamRepo.Setup(r => r.GetAll()).Returns(new List<Team>
        {
            new Team { Id = "team-1", Name = "Engineering", LeaderId = "other-leader" }
        });

        var handler = new PermissionAuthorizationHandler(_mockEmployeeRepo.Object, mockTeamRepo.Object);

        var requirement = new PermissionRequirement(AppPermissions.Timesheets.ViewReport);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId)
        };
        var context = CreateContext(requirement, claims);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    #endregion

    #region Suspended Read-Only Mode & Banned/Inactive Enforcement

    [Theory]
    [InlineData(AppPermissions.Chat.Use, true)]
    [InlineData(AppPermissions.Chat.ViewArchive, true)]
    [InlineData(AppPermissions.Albums.View, true)]
    [InlineData(AppPermissions.Calendar.View, true)]
    [InlineData(AppPermissions.Projects.View, true)]
    [InlineData(AppPermissions.AddressBook.View, true)]
    [InlineData(AppPermissions.Purchases.ViewPOs, true)]
    [InlineData(AppPermissions.Purchases.ViewBills, true)]
    [InlineData(AppPermissions.BusinessDocuments.View, true)]
    [InlineData(AppPermissions.Timesheets.ViewReport, true)]
    [InlineData(AppPermissions.Email.Use, true)]
    [InlineData(AppPermissions.Projects.Create, false)]
    [InlineData(AppPermissions.Purchases.ManagePOs, false)]
    [InlineData(AppPermissions.Chat.CreateChannels, false)]
    [InlineData(AppPermissions.AddressBook.Manage, false)]
    [InlineData(AppPermissions.Admin.ManageUsers, false)]
    [InlineData(AppPermissions.Admin.RoleName, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsReadOnlyAllowed_ReturnsExpectedClassification(string? permission, bool expected)
    {
        Assert.Equal(expected, AppPermissions.IsReadOnlyAllowed(permission));
    }

    [Fact]
    public async Task HandleRequirementAsync_SuspendedEmployee_AllowsReadOnlyPermissionsTheyHold_AndBlocksWritePermissions()
    {
        const string employeeId = "emp-suspended-rw";
        var employee = new Employee
        {
            Id = employeeId,
            IsActive = true,
            IsSuspended = true,
            Permissions = new List<string>
            {
                AppPermissions.Projects.View,
                AppPermissions.Projects.Create,
                AppPermissions.Chat.Use,
                AppPermissions.Chat.CreateChannels
            }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);
        var claims = new[] { new Claim("EmployeeId", employeeId) };

        // Read-only permissions held by the employee succeed
        var viewProjectsCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.View), claims);
        await _handler.HandleAsync(viewProjectsCtx);
        Assert.True(viewProjectsCtx.HasSucceeded);

        var chatUseCtx = CreateContext(new PermissionRequirement(AppPermissions.Chat.Use), claims);
        await _handler.HandleAsync(chatUseCtx);
        Assert.True(chatUseCtx.HasSucceeded);

        // Read-only permissions NOT held by the employee still fail
        var viewAlbumsCtx = CreateContext(new PermissionRequirement(AppPermissions.Albums.View), claims);
        await _handler.HandleAsync(viewAlbumsCtx);
        Assert.False(viewAlbumsCtx.HasSucceeded);

        // Write/create permissions held by the employee are blocked in Read-Only Mode
        var createProjectsCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.Create), claims);
        await _handler.HandleAsync(createProjectsCtx);
        Assert.False(createProjectsCtx.HasSucceeded);

        var createChannelsCtx = CreateContext(new PermissionRequirement(AppPermissions.Chat.CreateChannels), claims);
        await _handler.HandleAsync(createChannelsCtx);
        Assert.False(createChannelsCtx.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_SuspendedEmployeeWithPermissionGroupAndManagePOs_AllowsReadOnlyViewPOsAndBlocksManagePOs()
    {
        const string employeeId = "emp-suspended-grp";
        var group = new PermissionGroup
        {
            Id = "grp-po",
            Name = "Purchasing",
            Permissions = new List<string> { AppPermissions.Purchases.ManagePOs, AppPermissions.Albums.View, AppPermissions.AddressBook.Manage }
        };
        var employee = new Employee
        {
            Id = employeeId,
            IsActive = true,
            IsSuspended = true,
            PermissionGroup = group
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);
        var claims = new[] { new Claim("EmployeeId", employeeId) };

        var viewPoCtx = CreateContext(new PermissionRequirement(AppPermissions.Purchases.ViewPOs), claims);
        await _handler.HandleAsync(viewPoCtx);
        Assert.True(viewPoCtx.HasSucceeded);

        var viewAlbumCtx = CreateContext(new PermissionRequirement(AppPermissions.Albums.View), claims);
        await _handler.HandleAsync(viewAlbumCtx);
        Assert.True(viewAlbumCtx.HasSucceeded);

        var managePoCtx = CreateContext(new PermissionRequirement(AppPermissions.Purchases.ManagePOs), claims);
        await _handler.HandleAsync(managePoCtx);
        Assert.False(managePoCtx.HasSucceeded);

        var createAlbumCtx = CreateContext(new PermissionRequirement(AppPermissions.AddressBook.Manage), claims);
        await _handler.HandleAsync(createAlbumCtx);
        Assert.False(createAlbumCtx.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_SuspendedAdmin_AllowsReadOnlyViews_AndBlocksAllAdminAndWritePermissions()
    {
        const string employeeId = "emp-suspended-admin";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = true,
            IsActive = true,
            IsSuspended = true
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);
        var claims = new[] { new Claim("EmployeeId", employeeId) };

        var viewProjectsCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.View), claims);
        await _handler.HandleAsync(viewProjectsCtx);
        Assert.True(viewProjectsCtx.HasSucceeded);

        var manageUsersCtx = CreateContext(new PermissionRequirement(AppPermissions.Admin.ManageUsers), claims);
        await _handler.HandleAsync(manageUsersCtx);
        Assert.False(manageUsersCtx.HasSucceeded);

        var createProjectsCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.Create), claims);
        await _handler.HandleAsync(createProjectsCtx);
        Assert.False(createProjectsCtx.HasSucceeded);
    }

    [Theory]
    [InlineData(false, false)] // Inactive
    [InlineData(true, true)]   // Banned
    public async Task HandleRequirementAsync_BannedOrInactiveEmployee_BlocksEvenReadOnlyPermissions(bool isActive, bool isBanned)
    {
        const string employeeId = "emp-blocked";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = true,
            IsActive = isActive,
            IsBanned = isBanned,
            Permissions = new List<string> { AppPermissions.Chat.Use, AppPermissions.Projects.View }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);
        var claims = new[] { new Claim("EmployeeId", employeeId) };

        var chatCtx = CreateContext(new PermissionRequirement(AppPermissions.Chat.Use), claims);
        await _handler.HandleAsync(chatCtx);
        Assert.False(chatCtx.HasSucceeded);

        var projectsCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.View), claims);
        await _handler.HandleAsync(projectsCtx);
        Assert.False(projectsCtx.HasSucceeded);
    }

    #endregion
}
