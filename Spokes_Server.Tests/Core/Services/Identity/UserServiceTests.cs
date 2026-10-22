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
using Spokes_Server.Core.Services.Migrations;

namespace Spokes_Server.Tests.Core.Services.Identity;

public class UserServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;

        private readonly EmployeeRepository _employees;
        private readonly OpenIdAccountRepository _openIdAccounts;
        private readonly CompanyProfileRepository _companyProfiles;
        private readonly TeamRepository _teams;
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
            _companyProfiles = new CompanyProfileRepository(_persistence, _config);
            _teams = new TeamRepository(_persistence, _config);
            _mockAuth = new Mock<AuthenticationStateProvider>();

            _service = new UserService(_mockAuth.Object, _employees, _openIdAccounts, _companyProfiles, _teams);
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
        public void Constructor_WithNullAuthStateProvider_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new UserService(null!, _employees, _openIdAccounts, _companyProfiles, _teams));
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
        public async Task IsAdminAsync_ReturnsFalse_WhenAdminIsInactive()
        {
            var sub = "admin-inactive-sub";
            _employees.Save(new Employee { Id = "admin-inactive", IsAdmin = true, IsActive = false });
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = "admin-inactive" });
            SetupUser(sub);

            Assert.False(await _service.IsAdminAsync());
        }

        [Fact]
        public async Task IsAdminAsync_ReturnsFalse_WhenAdminIsSuspended()
        {
            var sub = "admin-suspended-sub";
            _employees.Save(new Employee { Id = "admin-suspended", IsAdmin = true, IsActive = true, IsSuspended = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = "admin-suspended" });
            SetupUser(sub);

            Assert.False(await _service.IsAdminAsync());
        }

        [Fact]
        public async Task IsAdminAsync_ReturnsFalse_WhenAdminIsBanned()
        {
            var sub = "admin-banned-sub";
            _employees.Save(new Employee { Id = "admin-banned", IsAdmin = true, IsActive = true, IsBanned = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = "admin-banned" });
            SetupUser(sub);

            Assert.False(await _service.IsAdminAsync());
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
            Assert.False(result.IsAdmin);
            Assert.True(result.IsActive);
            Assert.True(result.IsSystem);
            Assert.Contains(AppPermissions.Projects.View, result.Permissions);
            Assert.Contains(AppPermissions.Purchases.ViewPOs, result.Permissions);
            Assert.Contains(AppPermissions.Purchases.ManagePOs, result.Permissions);
            Assert.Contains(AppPermissions.BusinessDocuments.View, result.Permissions);
        }

        [Fact]
        public void GetEmployee_HeadlessRenderer_WithPermissionClaims_PopulatesPermissionsFromClaims()
        {
            var identity = new ClaimsIdentity([
                new Claim("sub", "headless-renderer"),
                new Claim("Spokes_InternalRenderer", "true"),
                new Claim("Permission", AppPermissions.Projects.View),
                new Claim("Permission", AppPermissions.Purchases.ViewPOs)
            ], "test");
            var user = new ClaimsPrincipal(identity);

            var result = _service.GetEmployee(user);

            Assert.NotNull(result);
            Assert.False(result.IsAdmin);
            Assert.True(result.IsSystem);
            Assert.Equal(2, result.Permissions.Count);
            Assert.Contains(AppPermissions.Projects.View, result.Permissions);
            Assert.Contains(AppPermissions.Purchases.ViewPOs, result.Permissions);
            Assert.DoesNotContain(AppPermissions.Purchases.ManagePOs, result.Permissions);
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
        public async Task LegacyOidcSubMigrationStep_MigratesLegacyEmployeeToOpenIdAccount()
        {
            var sub = "sub-legacy-read";
            var emp = new Employee { Id = "emp-legacy-read-1", MigratedOidcSub = sub, FirstName = "Legacy", LastName = "User", Email = "legacy@example.com" };
            _employees.Save(emp);

            var step = new LegacyOidcSubMigrationStep(_employees, _openIdAccounts);
            Assert.Equal("20260107_LegacyOidcSubToOpenIdAccount", step.MigrationKey);
            Assert.Equal(70, step.Order);

            await step.ExecuteAsync();

            var result = _service.GetBySub(sub);
            Assert.NotNull(result);
            Assert.Equal(emp.Id, result.Id);

            var account = _openIdAccounts.GetBySub(sub);
            Assert.NotNull(account);
            Assert.Equal(emp.Id, account.LinkedEmployeeId);
            Assert.Equal(sub, account.Sub);

            // Migrated employee field was cleared
            var updatedEmp = _employees.GetById(emp.Id);
            Assert.NotNull(updatedEmp);
            Assert.Null(updatedEmp.MigratedOidcSub);
        }

        [Fact]
        public async Task LegacyOidcSubMigrationStep_LinksExistingUnlinkedOpenIdAccount()
        {
            var sub = "sub-legacy";
            var emp = new Employee { Id = "emp-legacy-1", MigratedOidcSub = sub, Email = "emp1@example.com" };
            _employees.Save(emp);

            var account = new OpenIdAccount { Sub = sub, LinkedEmployeeId = "" };
            _openIdAccounts.Save(account);

            var step = new LegacyOidcSubMigrationStep(_employees, _openIdAccounts);
            await step.ExecuteAsync();

            var result = _service.GetBySub(sub);
            Assert.NotNull(result);
            Assert.Equal(emp.Id, result.Id);

            var updatedAccount = _openIdAccounts.GetBySub(sub);
            Assert.NotNull(updatedAccount);
            Assert.Equal(emp.Id, updatedAccount.LinkedEmployeeId);
        }

        [Fact]
        public void GetBySub_ReturnsNull_WhenNoOpenIdAccountMatches()
        {
            var result = _service.GetBySub("non-existent-sub");
            Assert.Null(result);
        }

        [Fact]
        public async Task CanViewProjectAsync_EmployeeNull_ReturnsFalse()
        {
            var identity = new ClaimsIdentity();
            var user = new ClaimsPrincipal(identity);
            _mockAuth.Setup(a => a.GetAuthenticationStateAsync())
                     .ReturnsAsync(new AuthenticationState(user));

            var project = new Project { Id = "proj-1", AccessPolicy = ProjectAccessPolicy.Public };

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

            var project = new Project { Id = "proj-restricted", AccessPolicy = ProjectAccessPolicy.Restricted };

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

            var project = new Project { Id = "proj-public", AccessPolicy = ProjectAccessPolicy.Public };

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

            var project = new Project { Id = "proj-public", AccessPolicy = ProjectAccessPolicy.Public };

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
                AccessPolicy = ProjectAccessPolicy.Restricted,
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
                AccessPolicy = ProjectAccessPolicy.Restricted,
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
                AccessPolicy = ProjectAccessPolicy.Restricted,
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
                AccessPolicy = ProjectAccessPolicy.Restricted,
                AllowedUserIds = ["other-user"],
                AllowedTeamIds = ["other-team"],
                ProjectManagerId = "other-pm"
            };

            var result = await _service.CanViewProjectAsync(project);

            Assert.False(result);
        }

        [Fact]
        public async Task CanViewProjectAsync_LeaderOfAllowedTeam_ReturnsTrue()
        {
            var sub = "leader-user";
            var emp = new Employee
            {
                Id = "emp-leader-1",
                TeamId = "other-team",
                IsAdmin = false,
                Permissions = [AppPermissions.Projects.View]
            };
            _employees.Save(emp);
            _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = emp.Id });
            SetupUser(sub);

            var team = new Team { Id = "allowed-team", LeaderId = emp.Id };
            _teams.Save(team);

            var project = new Project
            {
                Id = "proj-team-led",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                AllowedTeamIds = ["allowed-team"]
            };

            var result = await _service.CanViewProjectAsync(project);

            Assert.True(result);
        }


        [Fact]
        public void ProvisionOrSyncOidcUser_EmptySub_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => _service.ProvisionOrSyncOidcUser("", "Alice", "alice@example.com"));
        }

        [Fact]
        public void ProvisionOrSyncOidcUser_FirstTimeLogin_CreatesOpenIdAccountAndAdminEmployee()
        {
            var result = _service.ProvisionOrSyncOidcUser("sub-alice", "Alice Smith", "alice@example.com");

            Assert.True(result.IsFirstTimeLogin);
            Assert.NotNull(result.Account);
            Assert.Equal("sub-alice", result.Account.Sub);
            Assert.Equal("Alice Smith", result.Account.Name);
            Assert.Equal("alice@example.com", result.Account.Email);
            Assert.NotNull(result.Employee);
            Assert.Equal("Alice", result.Employee.FirstName);
            Assert.Equal("Smith", result.Employee.LastName);
            Assert.Equal("alice@example.com", result.Employee.Email);
            Assert.True(result.Employee.IsActive);
            Assert.True(result.Employee.IsAdmin); // First employee is admin
            Assert.Equal(result.Employee.Id, result.Account.LinkedEmployeeId);
        }

        [Fact]
        public void ProvisionOrSyncOidcUser_SecondEmployee_CreatesNonAdminWithDefaultPermissions()
        {
            // First user becomes admin
            _service.ProvisionOrSyncOidcUser("sub-first", "First Admin", "admin@example.com");

            // Second user becomes non-admin
            var result = _service.ProvisionOrSyncOidcUser("sub-second", "Bob Jones", "bob@example.com");

            Assert.True(result.IsFirstTimeLogin);
            Assert.NotNull(result.Employee);
            Assert.False(result.Employee.IsAdmin);
            Assert.Contains(AppPermissions.Chat.Use, result.Employee.Permissions);
            Assert.Contains(AppPermissions.Calendar.View, result.Employee.Permissions);
        }

        [Fact]
        public void ProvisionOrSyncOidcUser_ExistingOpenIdAccount_UpdatesNameAndEmail()
        {
            _service.ProvisionOrSyncOidcUser("sub-repeat", "Old Name", "old@example.com");

            var result = _service.ProvisionOrSyncOidcUser("sub-repeat", "New Name", "new@example.com");

            Assert.False(result.IsFirstTimeLogin);
            Assert.Equal("New Name", result.Account.Name);
            Assert.Equal("new@example.com", result.Account.Email);
        }

        [Fact]
        public void ProvisionOrSyncOidcUser_ExistingEmployeeMatchedByEmail_LinksToOpenIdAccount()
        {
            var emp = new Employee
            {
                Id = "emp-pre-existing",
                FirstName = "Existing",
                LastName = "User",
                Email = "match@example.com",
                IsActive = true
            };
            _employees.Save(emp);

            var result = _service.ProvisionOrSyncOidcUser("sub-match", "Match User", "match@example.com");

            Assert.NotNull(result.Employee);
            Assert.Equal(emp.Id, result.Employee.Id);
            Assert.Equal(emp.Id, result.Account.LinkedEmployeeId);
        }
    }
