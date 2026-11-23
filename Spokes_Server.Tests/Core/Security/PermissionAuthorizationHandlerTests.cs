namespace Spokes_Server.Tests.Core.Security;

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Security;
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
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");

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

    #region Fail-closed rejection of system-renderer and DocumentExport RenderScope confinement

    [Fact]
    public async Task HandleRequirementAsync_SystemRenderer_NeverSucceedsEvenWithAdminAndPermissionClaims()
    {
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("Permission", AppPermissions.Projects.View),
            new Claim("Spokes_InternalRenderer", "true"),
            new Claim("RenderScope", "DocumentExport")
        };

        var viewCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.View), claims);
        await _handler.HandleAsync(viewCtx);
        Assert.False(viewCtx.HasSucceeded);

        var adminCtx = CreateContext(new PermissionRequirement(AppPermissions.Admin.RoleName), claims);
        await _handler.HandleAsync(adminCtx);
        Assert.False(adminCtx.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_DocumentExportScope_EnforcesLiveEmployeeAndReadOnlyExportAllowlist()
    {
        var admin = new Employee
        {
            Id = "emp-export-admin",
            IsAdmin = true,
            IsActive = true
        };
        _mockEmployeeRepo.Setup(r => r.GetById(admin.Id)).Returns(admin);

        var poManager = new Employee
        {
            Id = "emp-export-po",
            IsActive = true,
            Permissions = [AppPermissions.Purchases.ManagePOs]
        };
        _mockEmployeeRepo.Setup(r => r.GetById(poManager.Id)).Returns(poManager);

        Claim[] AdminExportClaims() =>
        [
            new Claim("EmployeeId", admin.Id),
            new Claim("Spokes_InternalRenderer", "true"),
            new Claim("RenderScope", "DocumentExport")
        ];

        // Read-only export permissions succeed for authorized live employee
        var allowedViewCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.View), AdminExportClaims());
        await _handler.HandleAsync(allowedViewCtx);
        Assert.True(allowedViewCtx.HasSucceeded);

        // Mutating, manage, email, and Admin policies are rejected under DocumentExport even for an Admin employee
        var blockedAdminCtx = CreateContext(new PermissionRequirement(AppPermissions.Admin.RoleName), AdminExportClaims());
        await _handler.HandleAsync(blockedAdminCtx);
        Assert.False(blockedAdminCtx.HasSucceeded);

        var blockedEditCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.Edit), AdminExportClaims());
        await _handler.HandleAsync(blockedEditCtx);
        Assert.False(blockedEditCtx.HasSucceeded);

        var blockedManagePoCtx = CreateContext(new PermissionRequirement(AppPermissions.Purchases.ManagePOs), AdminExportClaims());
        await _handler.HandleAsync(blockedManagePoCtx);
        Assert.False(blockedManagePoCtx.HasSucceeded);

        // PO manager can render ViewPOs under DocumentExport without ManagePOs succeeding
        Claim[] poClaims =
        [
            new Claim("EmployeeId", poManager.Id),
            new Claim("Spokes_InternalRenderer", "true"),
            new Claim("RenderScope", "DocumentExport")
        ];
        var allowedPoViewCtx = CreateContext(new PermissionRequirement(AppPermissions.Purchases.ViewPOs), poClaims);
        await _handler.HandleAsync(allowedPoViewCtx);
        Assert.True(allowedPoViewCtx.HasSucceeded);

        var deniedProjectCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.View), poClaims);
        await _handler.HandleAsync(deniedProjectCtx);
        Assert.False(deniedProjectCtx.HasSucceeded);
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

    [Theory]
    [InlineData("emp-admin-suspended", true, true, false)]
    [InlineData("emp-admin-banned", true, false, true)]
    [InlineData("emp-admin-inactive", false, false, false)]
    public async Task HandleRequirementAsync_AdminRequirement_DisallowedEmployeeStatus_DoesNotSucceed(string employeeId, bool isActive, bool isSuspended, bool isBanned)
    {
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = true,
            IsActive = isActive,
            IsSuspended = isSuspended,
            IsBanned = isBanned
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

    [Theory]
    [InlineData("emp-bill-mgr", AppPermissions.Purchases.ManageBills)]
    [InlineData("emp-bill-viewer", AppPermissions.Purchases.ViewBills)]
    [InlineData("emp-bill-approver", AppPermissions.Purchases.ApproveBills)]
    public async Task HandleRequirementAsync_ViewPOs_EmployeeHasOnlyBillsPermission_DoesNotSucceed(string employeeId, string permission)
    {
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string> { permission }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[] { new Claim("EmployeeId", employeeId) };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(null, AppPermissions.Purchases.ViewBills)]
    [InlineData(null, AppPermissions.Purchases.ApproveBills)]
    [InlineData("system-renderer", AppPermissions.Purchases.ViewBills)]
    [InlineData("system-renderer", AppPermissions.Purchases.ManagePOs)]
    public async Task HandleRequirementAsync_ViewPOs_MissingOrSystemRendererEmployeeId_DoesNotSucceed(string? employeeId, string permissionClaim)
    {
        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = employeeId == null
            ? new[] { new Claim("Permission", permissionClaim) }
            : new[] { new Claim("EmployeeId", employeeId), new Claim("Permission", permissionClaim) };
        var context = CreateContext(requirement, claims);

        await _handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ViewPOs_DelegatedDocumentExport_UserHasManagePOsInRepo_Succeeds()
    {
        const string employeeId = "emp-render-po-mgr";
        var employee = new Employee
        {
            Id = employeeId,
            IsActive = true,
            IsAdmin = false,
            Permissions = new List<string> { AppPermissions.Purchases.ManagePOs }
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Purchases.ViewPOs);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId),
            new Claim("Spokes_InternalRenderer", "true"),
            new Claim("RenderScope", Spokes_Server.Core.Middleware.RenderTokenMiddleware.DocumentExportScope)
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
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
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
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
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
            PermissionGroupIds = [group.Id],
            PermissionGroups = [group]
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

    [Fact]
    public async Task HandleRequirementAsync_WithSessionService_RequiresActiveUnrevokedUnexpiredDeviceSession()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"Spokes_PermSess_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        using var persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = tempDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"})
                .Build();
            var sessionRepo = new DeviceSessionRepository(persistence, config);
            var empRepo = new EmployeeRepository(persistence, config);
            var sessionService = new SessionService(sessionRepo, empRepo);
            var handler = new PermissionAuthorizationHandler(empRepo, sessionService: sessionService);

            const string empId = "emp-sess-admin";
            empRepo.Save(new Employee { Id = empId, IsAdmin = true, IsActive = true });

            var activeSession = new DeviceSession
            {
                Id = "sess-active-1",
                EmployeeId = empId,
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            };
            sessionRepo.Save(activeSession);

            var activeClaims = new[]
            {
                new Claim("EmployeeId", empId),
                new Claim("SessionId", activeSession.Id)
            };
            var okCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.View), activeClaims);
            await handler.HandleAsync(okCtx);
            Assert.True(okCtx.HasSucceeded);

            // Revoke the session -> existing principal must now fail authorization
            activeSession.RevokedAt = DateTime.UtcNow;
            sessionRepo.Save(activeSession);

            var revokedCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.View), activeClaims);
            await handler.HandleAsync(revokedCtx);
            Assert.False(revokedCtx.HasSucceeded);

            // Expired session must also fail authorization
            var expiredSession = new DeviceSession
            {
                Id = "sess-expired-1",
                EmployeeId = empId,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-5)
            };
            sessionRepo.Save(expiredSession);

            var expiredClaims = new[]
            {
                new Claim("EmployeeId", empId),
                new Claim("SessionId", expiredSession.Id)
            };
            var expiredCtx = CreateContext(new PermissionRequirement(AppPermissions.Projects.View), expiredClaims);
            await handler.HandleAsync(expiredCtx);
            Assert.False(expiredCtx.HasSucceeded);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    #endregion
}
