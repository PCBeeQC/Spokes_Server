using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

public class UserServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;

        private readonly EmployeeRepository _employees;
        private readonly OpenIdAccountRepository _openIdAccounts;
        private readonly Mock<AuthenticationStateProvider> _mockAuth;
        private readonly UserService _service;

        public UserServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_User_{Guid.NewGuid()}");
            Directory.CreateDirectory(_testDataDir);

            Dictionary<string, string?> configDict = new() { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _employees = new EmployeeRepository(_persistence, _config);
            _openIdAccounts = new OpenIdAccountRepository(_persistence, _config);
            _mockAuth = new Mock<AuthenticationStateProvider>();

            _service = new UserService(_mockAuth.Object, _employees, _openIdAccounts);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        private void SetupUser(string sub)
        {
            var identity = new ClaimsIdentity([new Claim("sub", sub)], "test");
            var user = new ClaimsPrincipal(identity);
            _mockAuth.Setup(a => a.GetAuthenticationStateAsync())
                     .ReturnsAsync(new AuthenticationState(user));
        }

        [Fact]
        public async Task GetEmployeeAsync_ReturnsLinkedEmployee_ViaOpenIdAccount()
        {
            var sub = "oidc-123";
            var empId = "emp-111";
            _employees.Save(new Employee { Id = empId, FirstName = "Linked", LastName = "User" });
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = empId });

            SetupUser(sub);

            var result = await _service.GetEmployeeAsync();
            Assert.NotNull(result);
            Assert.Equal(empId, result.Id);
        }

        [Fact]
        public async Task GetBySub_ReturnsNull_WhenNoLinkageExists()
        {
            var sub = "legacy-sub";
            var empId = "emp-legacy";
            _employees.Save(new Employee { Id = empId });

            var result = _service.GetBySub(sub);
            Assert.Null(result);
        }

        [Fact]
        public async Task HasPermissionAsync_ReturnsTrue_WhenEmployeeHasPermission()
        {
            var sub = "perm-user";
            var emp = new Employee { Id = "e", Permissions = ["do.it"] };
            _employees.Save(emp);
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = "e" });
            SetupUser(sub);

            Assert.True(await _service.HasPermissionAsync("do.it"));
            Assert.False(await _service.HasPermissionAsync("not.allowed"));
        }

        [Fact]
        public async Task IsAdminAsync_ReturnsTrue_ForAdminEmployee()
        {
            var sub = "admin-sub";
            _employees.Save(new Employee { Id = "a", IsAdmin = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = "a" });
            SetupUser(sub);

            Assert.True(await _service.IsAdminAsync());
        }

        [Fact]
        public async Task GetEmployeeAsync_CachesResult()
        {
            SetupUser("sub-1");
            _employees.Save(new Employee { Id = "e1" });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "sub-1", LinkedEmployeeId = "e1" });

            var first = await _service.GetEmployeeAsync();
            var second = await _service.GetEmployeeAsync();

            Assert.Same(first, second);
            _mockAuth.Verify(a => a.GetAuthenticationStateAsync(), Times.Once);
        }

        [Fact]
        public async Task ClearCache_ForcesRefetch()
        {
            SetupUser("sub-1");
            _employees.Save(new Employee { Id = "e1" });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "sub-1", LinkedEmployeeId = "e1" });

            await _service.GetEmployeeAsync();
            _service.ClearCache();
            await _service.GetEmployeeAsync();

            _mockAuth.Verify(a => a.GetAuthenticationStateAsync(), Times.Exactly(2));
        }

        [Fact]
        public async Task GetEmployeeAsync_Unauthenticated_ReturnsNull()
        {
            var identity = new ClaimsIdentity();
            var user = new ClaimsPrincipal(identity);
            _mockAuth.Setup(a => a.GetAuthenticationStateAsync())
                     .ReturnsAsync(new AuthenticationState(user));

            var result = await _service.GetEmployeeAsync();

            Assert.Null(result);
        }

        [Fact]
        public async Task GetEmployeeAsync_HeadlessRenderer_WithInternalClaim_ReturnsSystemVirtualEmployee()
        {
            var identity = new ClaimsIdentity([
                new Claim("sub", "headless-renderer"),
                new Claim("Spokes_InternalRenderer", "true")
            ], "test");
            var user = new ClaimsPrincipal(identity);
            _mockAuth.Setup(a => a.GetAuthenticationStateAsync())
                     .ReturnsAsync(new AuthenticationState(user));

            var result = await _service.GetEmployeeAsync();

            Assert.NotNull(result);
            Assert.Equal("system-renderer", result.Id);
            Assert.Equal("System", result.FirstName);
            Assert.Equal("Renderer", result.LastName);
            Assert.Equal("renderer@system.local", result.Email);
            Assert.True(result.IsAdmin);
            Assert.True(result.IsActive);
        }

        [Fact]
        public async Task GetEmployeeAsync_HeadlessRenderer_WithoutInternalClaim_ReturnsNull()
        {
            SetupUser("headless-renderer");

            var result = await _service.GetEmployeeAsync();

            Assert.Null(result);
        }

        [Fact]
        public async Task GetSessionIdAsync_Unauthenticated_ReturnsNull()
        {
            var identity = new ClaimsIdentity();
            var user = new ClaimsPrincipal(identity);
            _mockAuth.Setup(a => a.GetAuthenticationStateAsync())
                     .ReturnsAsync(new AuthenticationState(user));

            var result = await _service.GetSessionIdAsync();

            Assert.Null(result);
        }

        [Fact]
        public async Task GetSessionIdAsync_Authenticated_ReturnsSessionIdClaim()
        {
            var identity = new ClaimsIdentity([new Claim("SessionId", "sess-123")], "test");
            var user = new ClaimsPrincipal(identity);
            _mockAuth.Setup(a => a.GetAuthenticationStateAsync())
                     .ReturnsAsync(new AuthenticationState(user));

            var result = await _service.GetSessionIdAsync();

            Assert.Equal("sess-123", result);
        }

        [Fact]
        public void GetEmployee_WithEmployeeIdClaim_ResolvesDirectly()
        {
            var empId = "emp-claim-1";
            var emp = new Employee { Id = empId, FirstName = "Direct", LastName = "User" };
            _employees.Save(emp);

            var identity = new ClaimsIdentity([new Claim("EmployeeId", empId)], "test");
            var user = new ClaimsPrincipal(identity);

            var result = _service.GetEmployee(user);

            Assert.NotNull(result);
            Assert.Equal(empId, result.Id);
            Assert.Equal("Direct", result.FirstName);
        }

        [Fact]
        public void GetBySub_SelfHealingLegacyLinkage_WhenOpenIdAccountExistsWithoutLinkedId()
        {
            var sub = "sub-legacy";
            var emp = new Employee { Id = "emp-legacy-1", OidcSub = sub };
            _employees.Save(emp);

            var account = new OpenIdAccount { Sub = sub, LinkedEmployeeId = null };
            _openIdAccounts.Save(account);

            var result = _service.GetBySub(sub);

            Assert.NotNull(result);
            Assert.Equal(emp.Id, result.Id);

            var updatedAccount = _openIdAccounts.GetBySub(sub);
            Assert.NotNull(updatedAccount);
            Assert.Equal(emp.Id, updatedAccount.LinkedEmployeeId);
        }

        [Fact]
        public void GetBySub_SelfHealingLegacyLinkage_WhenNoOpenIdAccountExists()
        {
            var sub = "sub-legacy-2";
            var emp = new Employee { Id = "emp-legacy-2", OidcSub = sub };
            _employees.Save(emp);

            var result = _service.GetBySub(sub);

            Assert.NotNull(result);
            Assert.Equal(emp.Id, result.Id);

            var createdAccount = _openIdAccounts.GetBySub(sub);
            Assert.NotNull(createdAccount);
            Assert.Equal(sub, createdAccount.Sub);
            Assert.Equal(emp.Id, createdAccount.LinkedEmployeeId);
        }

        [Fact]
        public async Task CanViewProjectAsync_EmployeeNull_ReturnsFalse()
        {
            var identity = new ClaimsIdentity();
            var user = new ClaimsPrincipal(identity);
            _mockAuth.Setup(a => a.GetAuthenticationStateAsync())
                     .ReturnsAsync(new AuthenticationState(user));

            var project = new Project { Id = "proj-1", AccessPolicy = "Public" };

            var result = await _service.CanViewProjectAsync(project);

            Assert.False(result);
        }

        [Fact]
        public async Task CanViewProjectAsync_Admin_ReturnsTrue()
        {
            var sub = "admin-user";
            var emp = new Employee { Id = "emp-admin", IsAdmin = true };
            _employees.Save(emp);
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = emp.Id });
            SetupUser(sub);

            var project = new Project { Id = "proj-restricted", AccessPolicy = "Restricted" };

            var result = await _service.CanViewProjectAsync(project);

            Assert.True(result);
        }

        [Fact]
        public async Task CanViewProjectAsync_MissingViewPermission_ReturnsFalse()
        {
            var sub = "user-no-view";
            var emp = new Employee { Id = "emp-no-view", IsAdmin = false, Permissions = [] };
            _employees.Save(emp);
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = emp.Id });
            SetupUser(sub);

            var project = new Project { Id = "proj-public", AccessPolicy = "Public" };

            var result = await _service.CanViewProjectAsync(project);

            Assert.False(result);
        }

        [Fact]
        public async Task CanViewProjectAsync_PublicPolicy_ReturnsTrue()
        {
            var sub = "user-view-public";
            var emp = new Employee
            {
                Id = "emp-view-1",
                IsAdmin = false,
                Permissions = [AppPermissions.Projects.View]
            };
            _employees.Save(emp);
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = emp.Id });
            SetupUser(sub);

            var project = new Project { Id = "proj-public", AccessPolicy = "Public" };

            var result = await _service.CanViewProjectAsync(project);

            Assert.True(result);
        }

        [Fact]
        public async Task CanViewProjectAsync_AllowedUser_ReturnsTrue()
        {
            var sub = "user-allowed";
            var emp = new Employee
            {
                Id = "emp-allowed-1",
                IsAdmin = false,
                Permissions = [AppPermissions.Projects.View]
            };
            _employees.Save(emp);
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = emp.Id });
            SetupUser(sub);

            var project = new Project
            {
                Id = "proj-restricted",
                AccessPolicy = "Restricted",
                AllowedUserIds = [emp.Id]
            };

            var result = await _service.CanViewProjectAsync(project);

            Assert.True(result);
        }

        [Fact]
        public async Task CanViewProjectAsync_AllowedTeam_ReturnsTrue()
        {
            var sub = "team-user";
            var emp = new Employee
            {
                Id = "emp-team-1",
                TeamId = "team-1",
                IsAdmin = false,
                Permissions = [AppPermissions.Projects.View]
            };
            _employees.Save(emp);
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = emp.Id });
            SetupUser(sub);

            var project = new Project
            {
                Id = "proj-team",
                AccessPolicy = "Restricted",
                AllowedTeamIds = ["team-1"]
            };

            var result = await _service.CanViewProjectAsync(project);

            Assert.True(result);
        }

        [Fact]
        public async Task CanViewProjectAsync_ProjectManager_ReturnsTrue()
        {
            var sub = "pm-user";
            var emp = new Employee
            {
                Id = "emp-pm-1",
                IsAdmin = false,
                Permissions = [AppPermissions.Projects.View]
            };
            _employees.Save(emp);
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = emp.Id });
            SetupUser(sub);

            var project = new Project
            {
                Id = "proj-pm",
                AccessPolicy = "Restricted",
                ProjectManagerId = emp.Id
            };

            var result = await _service.CanViewProjectAsync(project);

            Assert.True(result);
        }

        [Fact]
        public async Task CanViewProjectAsync_RestrictedProject_ReturnsFalse()
        {
            var sub = "restricted-user";
            var emp = new Employee
            {
                Id = "emp-restricted-1",
                TeamId = "team-user",
                IsAdmin = false,
                Permissions = [AppPermissions.Projects.View]
            };
            _employees.Save(emp);
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = emp.Id });
            SetupUser(sub);

            var project = new Project
            {
                Id = "proj-restricted",
                AccessPolicy = "Restricted",
                AllowedUserIds = ["other-user"],
                AllowedTeamIds = ["other-team"],
                ProjectManagerId = "other-pm"
            };

            var result = await _service.CanViewProjectAsync(project);

            Assert.False(result);
        }
    }
