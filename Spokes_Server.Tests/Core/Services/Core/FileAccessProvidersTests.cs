using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Constants;
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
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Services.Core
{
    public class FileAccessProvidersTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;

        private readonly ProjectRepository _projects;
        private readonly TeamRepository _teams;
        private readonly ExpenseReportRepository _expenseReports;
        private readonly ChatChannelRepository _channels;

        public FileAccessProvidersTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_FileAccess_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _projects = new ProjectRepository(_persistence, _config);
            _teams = new TeamRepository(_persistence, _config);

            var mockSequence = new Mock<SequenceService>(_config);
            _expenseReports = new ExpenseReportRepository(_persistence, _config, mockSequence.Object);

            _channels = new ChatChannelRepository(_persistence, _config);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        [Fact]
        public async Task ProjectFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = await provider.CanAccessAsync(admin, "any-project");
            Assert.True(result);
        }

        [Fact]
        public async Task ProjectFileAccessProvider_ReturnsTrueForAllowedUser()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false };
            var project = new Project { Id = "p1", AllowedUserIds = new List<string> { "user-1" } };
            _projects.Save(project);

            var result = await provider.CanAccessAsync(user, "p1");
            Assert.True(result);
        }

        [Fact]
        public async Task ExpenseFileAccessProvider_ReturnsTrueForOwner()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var user = new Employee { Id = "user-1", IsAdmin = false };
            var report = new ExpenseReport { Id = "e1", EmployeeId = "user-1" };
            _expenseReports.Save(report);

            var result = await provider.CanAccessAsync(user, "e1");
            Assert.True(result);
        }

        [Fact]
        public async Task ExpenseFileAccessProvider_ReturnsFalseForOthers()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var user = new Employee { Id = "user-other", IsAdmin = false };
            var report = new ExpenseReport { Id = "e1", EmployeeId = "user-1" };
            _expenseReports.Save(report);

            var result = await provider.CanAccessAsync(user, "e1");
            Assert.False(result);
        }

        [Fact]
        public async Task BillFileAccessProvider_ReturnsTrueForFinance()
        {
            var provider = new BillFileAccessProvider();
            var financeUser = new Employee { Id = "f1", IsAdmin = false, Permissions = new List<string> { AppPermissions.Finance.AccessAccounting } };

            var result = await provider.CanAccessAsync(financeUser, "any-bill");
            Assert.True(result);
        }

        [Fact]
        public async Task ChatFileAccessProvider_ReturnsTrueForParticipants()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser(It.IsAny<string>()))
                .Returns((string uid) => _channels.GetDirectChannelsForUser(uid).Concat(_channels.GetChannelsForUser(uid, new List<string>(), new List<string>())).ToList());

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var user = new Employee { Id = "user-1" };
            var channel = new ChatChannel { Id = "c1", ParticipantIds = new List<string> { "user-1" }, ChannelType = ChatChannelType.Direct };
            _channels.Save(channel);

            var result = await provider.CanAccessAsync(user, "c1");
            Assert.True(result);
        }

        [Fact]
        public async Task ChatFileAccessProvider_ReturnsTrueForGeneralChannel()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser(It.IsAny<string>()))
                .Returns((string uid) => _channels.GetDirectChannelsForUser(uid).Concat(_channels.GetChannelsForUser(uid, new List<string>(), new List<string>())).ToList());

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var user = new Employee { Id = "user-stranger" };
            var channel = new ChatChannel { Id = "c2", ChannelType = ChatChannelType.General };
            _channels.Save(channel);

            var result = await provider.CanAccessAsync(user, "c2");
            Assert.True(result);
        }

        [Fact]
        public async Task RecurringCostFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new RecurringCostFileAccessProvider();
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = await provider.CanAccessAsync(admin, "any-cost");
            Assert.True(result);
        }

        [Fact]
        public async Task ProjectNoteFileAccessProvider_ReturnsTrueForAllowedUser()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false };
            var project = new Project { Id = "p1", AllowedUserIds = new List<string> { "user-1" } };
            _projects.Save(project);

            var result = await provider.CanAccessAsync(user, "p1");
            Assert.True(result);
        }

        [Fact]
        public async Task AlbumFileAccessProvider_ReturnsTrueForChannelMembersAndFalseForNonMembers()
        {
            var albumsRepo = new AlbumRepository(_persistence, _config);
            var mockChatAccess = new Mock<IChatChannelAccessService>();

            mockChatAccess.Setup(s => s.GetUsersForChannel("chan-shared"))
                .Returns(new List<string> { "user-in-channel" });
            mockChatAccess.Setup(s => s.GetUsersForChannel("other-chan"))
                .Returns(new List<string> { "other-user" });

            var provider = new AlbumFileAccessProvider(albumsRepo, mockChatAccess.Object);

            var album = new Album
            {
                Id = "album-1",
                OwnerId = "user-owner",
                ContributorUserIds = new List<string> { "user-contrib" },
                SharedWithChannelIds = new List<string> { "chan-shared" }
            };
            albumsRepo.Save(album);

            // Owner access
            Assert.True(await provider.CanAccessAsync(new Employee { Id = "user-owner" }, "album-1"));

            // Contributor access
            Assert.True(await provider.CanAccessAsync(new Employee { Id = "user-contrib" }, "album-1"));

            // Channel member access
            Assert.True(await provider.CanAccessAsync(new Employee { Id = "user-in-channel" }, "album-1"));

            // Non-member access denied
            Assert.False(await provider.CanAccessAsync(new Employee { Id = "user-outsider" }, "album-1"));
        }

        [Fact]
        public async Task TempFileAccessProvider_ReturnsTrueForActiveEmployee()
        {
            var provider = new TempFileAccessProvider();
            var employee = new Employee { Id = "emp1", IsActive = true, IsSuspended = false, IsBanned = false };

            var result = await provider.CanAccessAsync(employee, Guid.NewGuid().ToString());
            Assert.True(result);
        }

        [Theory]
        [InlineData(false, false, false)] // Inactive
        [InlineData(true, true, false)]  // Suspended
        [InlineData(true, false, true)]  // Banned
        public async Task TempFileAccessProvider_ReturnsFalseForInactiveSuspendedOrBannedEmployee(bool isActive, bool isSuspended, bool isBanned)
        {
            var provider = new TempFileAccessProvider();
            var employee = new Employee { Id = "emp1", IsActive = isActive, IsSuspended = isSuspended, IsBanned = isBanned };

            var result = await provider.CanAccessAsync(employee, Guid.NewGuid().ToString());
            Assert.False(result);
        }

        [Fact]
        public async Task TempFileAccessProvider_ReturnsFalseForNullUser()
        {
            var provider = new TempFileAccessProvider();
            var result = await provider.CanAccessAsync(null!, Guid.NewGuid().ToString());
            Assert.False(result);
        }
    }
}



