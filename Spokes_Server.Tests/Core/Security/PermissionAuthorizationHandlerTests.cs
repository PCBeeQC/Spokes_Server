namespace Spokes_Server.Tests.Core.Security;

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;

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
        _db = new Database(null!, _mockEmployeeRepo.Object);
        _handler = new PermissionAuthorizationHandler(_db);
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
        // Arrange & Act
        var requirement = new PermissionRequirement("Projects.View");

        // Assert
        Assert.Equal("Projects.View", requirement.Permission);
    }

    #region Fallback when EmployeeId claim is missing

    [Fact]
    public async Task HandleRequirementAsync_MissingEmployeeId_UserHasRoleAdmin_Succeeds()
    {
        // Arrange
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim(ClaimTypes.Role, "Admin")
        };
        var context = CreateContext(requirement, claims);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_MissingEmployeeId_UserHasMatchingPermissionClaim_Succeeds()
    {
        // Arrange
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("Permission", AppPermissions.Projects.View)
        };
        var context = CreateContext(requirement, claims);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_MissingEmployeeId_UserLacksRoleAndMatchingClaim_DoesNotSucceed()
    {
        // Arrange
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("Permission", AppPermissions.Chat.Use)
        };
        var context = CreateContext(requirement, claims);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_MissingEmployeeId_AnonymousUser_DoesNotSucceed()
    {
        // Arrange
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var context = CreateContext(requirement, null);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
    }

    #endregion

    #region Fallback when EmployeeId claim is system-renderer

    [Fact]
    public async Task HandleRequirementAsync_SystemRenderer_UserHasRoleAdmin_Succeeds()
    {
        // Arrange
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var context = CreateContext(requirement, claims);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_SystemRenderer_UserHasMatchingPermissionClaim_Succeeds()
    {
        // Arrange
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim("Permission", AppPermissions.Projects.View)
        };
        var context = CreateContext(requirement, claims);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_SystemRenderer_UserLacksRoleAndMatchingClaim_DoesNotSucceed()
    {
        // Arrange
        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", "system-renderer"),
            new Claim("Permission", AppPermissions.Chat.Use)
        };
        var context = CreateContext(requirement, claims);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
    }

    #endregion

    #region Valid EmployeeId claim

    [Fact]
    public async Task HandleRequirementAsync_ValidEmployeeId_EmployeeNotFoundInDatabase_DoesNotSucceed()
    {
        // Arrange
        const string employeeId = "emp-unknown";
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns((Employee?)null);

        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId),
            new Claim(ClaimTypes.Role, "Admin") // Claims should not bypass missing DB employee
        };
        var context = CreateContext(requirement, claims);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminRequirement_EmployeeIsAdmin_Succeeds()
    {
        // Arrange
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

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminRequirement_EmployeeIsNotAdmin_DoesNotSucceed()
    {
        // Arrange
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

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_RegularPermission_EmployeeHasPermission_Succeeds()
    {
        // Arrange
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

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_RegularPermission_EmployeeLacksPermission_DoesNotSucceed()
    {
        // Arrange
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

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_RegularPermission_EmployeeIsAdmin_Succeeds()
    {
        // Arrange - Admin implicitly has all permissions via Employee.HasPermission
        const string employeeId = "emp-admin-regular";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = true,
            IsActive = true,
            Permissions = new List<string>() // Empty explicit permissions
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId)
        };
        var context = CreateContext(requirement, claims);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_ValidEmployeeId_UserHasPermissionClaim_StillFailsIfEmployeeLacksPermissionInDb()
    {
        // Arrange - Verifies claims are ignored in favor of live DB state when valid EmployeeId is present
        const string employeeId = "emp-worker";
        var employee = new Employee
        {
            Id = employeeId,
            IsAdmin = false,
            IsActive = true,
            Permissions = new List<string>() // DB says employee has NO permissions
        };
        _mockEmployeeRepo.Setup(r => r.GetById(employeeId)).Returns(employee);

        var requirement = new PermissionRequirement(AppPermissions.Projects.View);
        var claims = new[]
        {
            new Claim("EmployeeId", employeeId),
            new Claim("Permission", AppPermissions.Projects.View) // Principal claims have permission, but DB does not
        };
        var context = CreateContext(requirement, claims);

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
    }

    #endregion
}
