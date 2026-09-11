using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Services.Core
{
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
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_User_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
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
            var identity = new ClaimsIdentity(new[] { new Claim("sub", sub) }, "test");
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
            var emp = new Employee { Id = "e", Permissions = new List<string> { "do.it" } };
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
    }
}



