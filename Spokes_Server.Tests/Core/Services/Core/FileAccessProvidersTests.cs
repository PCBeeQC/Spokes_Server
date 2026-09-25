using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

public class FileAccessProvidersTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;

    private readonly ProjectRepository _projects;
    private readonly TeamRepository _teams;
    private readonly ExpenseReportRepository _expenseReports;
    private readonly ChatChannelRepository _channels;
    private readonly AlbumRepository _albums;

    public FileAccessProvidersTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_FileAccess_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _projects = new ProjectRepository(_persistence, _config);
            _teams = new TeamRepository(_persistence, _config);

            var mockSequence = new Mock<SequenceService>(_config);
            _expenseReports = new ExpenseReportRepository(_persistence, _config, mockSequence.Object);

            _channels = new ChatChannelRepository(_persistence, _config);
            _albums = new AlbumRepository(_persistence, _config);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        #region Category Properties Tests

        [Fact]
        public void ProjectFileAccessProvider_Category_ReturnsProjects()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            Assert.Equal("projects", provider.Category);
        }

        [Fact]
        public void ExpenseFileAccessProvider_Category_ReturnsExpenses()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            Assert.Equal("expenses", provider.Category);
        }

        [Fact]
        public void BillFileAccessProvider_Category_ReturnsBills()
        {
            var provider = new BillFileAccessProvider();
            Assert.Equal("bills", provider.Category);
        }

        [Fact]
        public void ChatFileAccessProvider_Category_ReturnsChat()
        {
            var provider = new ChatFileAccessProvider(new Mock<IChatChannelAccessService>().Object);
            Assert.Equal("chat", provider.Category);
        }

        [Fact]
        public void RecurringCostFileAccessProvider_Category_ReturnsRecurringCosts()
        {
            var provider = new RecurringCostFileAccessProvider();
            Assert.Equal("recurringcosts", provider.Category);
        }

        [Fact]
        public void ProjectNoteFileAccessProvider_Category_ReturnsProjectNotes()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            Assert.Equal("projectnotes", provider.Category);
        }

        [Fact]
        public void AvatarFileAccessProvider_Category_ReturnsAvatars()
        {
            var provider = new AvatarFileAccessProvider();
            Assert.Equal("avatars", provider.Category);
        }

        [Fact]
        public void SignatureFileAccessProvider_Category_ReturnsSignatures()
        {
            var provider = new SignatureFileAccessProvider();
            Assert.Equal("signatures", provider.Category);
        }

        [Fact]
        public void AlbumFileAccessProvider_Category_ReturnsAlbums()
        {
            var provider = new AlbumFileAccessProvider(_albums, new Mock<IChatChannelAccessService>().Object);
            Assert.Equal("albums", provider.Category);
        }

        [Fact]
        public void TempFileAccessProvider_Category_ReturnsTemp()
        {
            var provider = new TempFileAccessProvider();
            Assert.Equal("temp", provider.Category);
        }

        #endregion

        #region AvatarFileAccessProvider Tests

        [Fact]
        public async Task AvatarFileAccessProvider_CanAccessAsync_ReturnsTrue()
        {
            var provider = new AvatarFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = await provider.CanAccessAsync(user, "any-context-id");
            Assert.True(result);
        }

        #endregion

        #region SignatureFileAccessProvider Tests

        [Fact]
        public async Task SignatureFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new SignatureFileAccessProvider();
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = await provider.CanAccessAsync(admin, "other-user-id");
            Assert.True(result);
        }

        [Fact]
        public async Task SignatureFileAccessProvider_ReturnsTrueWhenUserMatchesContextId()
        {
            var provider = new SignatureFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = await provider.CanAccessAsync(user, "user-1");
            Assert.True(result);
        }

        [Fact]
        public async Task SignatureFileAccessProvider_ReturnsTrueForFinance()
        {
            var provider = new SignatureFileAccessProvider();
            var financeUser = new Employee
            {
                Id = "finance-1",
                IsAdmin = false,
                Permissions = new List<string> { AppPermissions.Finance.AccessAccounting }
            };

            var result = await provider.CanAccessAsync(financeUser, "other-user-id");
            Assert.True(result);
        }

        [Fact]
        public async Task SignatureFileAccessProvider_ReturnsFalseForUnauthorizedUser()
        {
            var provider = new SignatureFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = new List<string>() };

            var result = await provider.CanAccessAsync(user, "other-user-id");
            Assert.False(result);
        }

        #endregion

        #region BillFileAccessProvider Tests

        [Fact]
        public async Task BillFileAccessProvider_ReturnsTrueForFinance()
        {
            var provider = new BillFileAccessProvider();
            var financeUser = new Employee { Id = "f1", IsAdmin = false, Permissions = new List<string> { AppPermissions.Finance.AccessAccounting } };

            var result = await provider.CanAccessAsync(financeUser, "any-bill");
            Assert.True(result);
        }

        [Fact]
        public async Task BillFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new BillFileAccessProvider();
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = await provider.CanAccessAsync(admin, "any-bill");
            Assert.True(result);
        }

        [Fact]
        public async Task BillFileAccessProvider_ReturnsFalseForUnauthorizedUser()
        {
            var provider = new BillFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = new List<string>() };

            var result = await provider.CanAccessAsync(user, "any-bill");
            Assert.False(result);
        }

        #endregion

        #region RecurringCostFileAccessProvider Tests

        [Fact]
        public async Task RecurringCostFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new RecurringCostFileAccessProvider();
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = await provider.CanAccessAsync(admin, "any-cost");
            Assert.True(result);
        }

        [Fact]
        public async Task RecurringCostFileAccessProvider_ReturnsTrueForFinance()
        {
            var provider = new RecurringCostFileAccessProvider();
            var financeUser = new Employee
            {
                Id = "finance-1",
                IsAdmin = false,
                Permissions = new List<string> { AppPermissions.Finance.AccessAccounting }
            };

            var result = await provider.CanAccessAsync(financeUser, "any-cost");
            Assert.True(result);
        }

        [Fact]
        public async Task RecurringCostFileAccessProvider_ReturnsFalseForUnauthorizedUser()
        {
            var provider = new RecurringCostFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = new List<string>() };

            var result = await provider.CanAccessAsync(user, "any-cost");
            Assert.False(result);
        }

        #endregion

        #region ExpenseFileAccessProvider Tests

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
        public async Task ExpenseFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = await provider.CanAccessAsync(admin, "non-existent-report");
            Assert.True(result);
        }

        [Fact]
        public async Task ExpenseFileAccessProvider_ReturnsTrueForFinance()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var financeUser = new Employee
            {
                Id = "finance-1",
                IsAdmin = false,
                Permissions = new List<string> { AppPermissions.Finance.AccessAccounting }
            };

            var result = await provider.CanAccessAsync(financeUser, "non-existent-report");
            Assert.True(result);
        }

        [Fact]
        public async Task ExpenseFileAccessProvider_ReturnsFalseForNonExistentReport()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = new List<string>() };

            var result = await provider.CanAccessAsync(user, "non-existent-report");
            Assert.False(result);
        }

        #endregion

        #region ProjectFileAccessProvider Tests

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
        public async Task ProjectFileAccessProvider_ReturnsFalseWhenProjectDoesNotExist()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = await provider.CanAccessAsync(user, "non-existent-project");
            Assert.False(result);
        }

        [Fact]
        public async Task ProjectFileAccessProvider_ReturnsTrueWhenProjectIsPublic()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false };
            var project = new Project { Id = "p-public", AccessPolicy = "Public" };
            _projects.Save(project);

            var result = await provider.CanAccessAsync(user, "p-public");
            Assert.True(result);
        }

        [Fact]
        public async Task ProjectFileAccessProvider_ReturnsTrueWhenUserTeamIsAllowed()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", TeamId = "team-1", IsAdmin = false };
            var project = new Project
            {
                Id = "p-team",
                AccessPolicy = "Restricted",
                AllowedTeamIds = new List<string> { "team-1" }
            };
            _projects.Save(project);

            var result = await provider.CanAccessAsync(user, "p-team");
            Assert.True(result);
        }

        [Fact]
        public async Task ProjectFileAccessProvider_ReturnsTrueWhenUserIsLeaderOfAllowedTeam()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var leader = new Employee { Id = "leader-1", TeamId = "team-other", IsAdmin = false };
            var team = new Team { Id = "team-1", LeaderId = "leader-1" };
            _teams.Save(team);

            var project = new Project
            {
                Id = "p-team-leader",
                AccessPolicy = "Restricted",
                AllowedTeamIds = new List<string> { "team-1" }
            };
            _projects.Save(project);

            var result = await provider.CanAccessAsync(leader, "p-team-leader");
            Assert.True(result);
        }

        [Fact]
        public async Task ProjectFileAccessProvider_ReturnsFalseWhenUserIsNotAllowed()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var outsider = new Employee { Id = "user-outsider", TeamId = "team-other", IsAdmin = false };
            var project = new Project
            {
                Id = "p-restricted",
                AccessPolicy = "Restricted",
                AllowedUserIds = new List<string> { "user-1" },
                AllowedTeamIds = new List<string> { "team-allowed" }
            };
            _projects.Save(project);

            var result = await provider.CanAccessAsync(outsider, "p-restricted");
            Assert.False(result);
        }

        #endregion

        #region ProjectNoteFileAccessProvider Tests

        [Fact]
        public async Task ProjectNoteFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = await provider.CanAccessAsync(admin, "any-project");
            Assert.True(result);
        }

        [Fact]
        public async Task ProjectNoteFileAccessProvider_ReturnsTrueForPublicProject()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false };
            var project = new Project { Id = "pn-public", AccessPolicy = "Public" };
            _projects.Save(project);

            var result = await provider.CanAccessAsync(user, "pn-public");
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
        public async Task ProjectNoteFileAccessProvider_ReturnsTrueForAllowedTeam()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", TeamId = "team-1", IsAdmin = false };
            var project = new Project
            {
                Id = "pn-team",
                AccessPolicy = "Restricted",
                AllowedTeamIds = new List<string> { "team-1" }
            };
            _projects.Save(project);

            var result = await provider.CanAccessAsync(user, "pn-team");
            Assert.True(result);
        }

        [Fact]
        public async Task ProjectNoteFileAccessProvider_ReturnsFalseForNonExistentProject()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = await provider.CanAccessAsync(user, "non-existent-project");
            Assert.False(result);
        }

        [Fact]
        public async Task ProjectNoteFileAccessProvider_ReturnsFalseForUnauthorizedUser()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-outsider", IsAdmin = false };
            var project = new Project
            {
                Id = "pn-restricted",
                AccessPolicy = "Restricted",
                AllowedUserIds = new List<string> { "user-1" }
            };
            _projects.Save(project);

            var result = await provider.CanAccessAsync(user, "pn-restricted");
            Assert.False(result);
        }

        #endregion

        #region ChatFileAccessProvider Tests

        [Fact]
        public async Task ChatFileAccessProvider_ReturnsTrueForAdmin_WhenChannelInAccessibleList()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser("admin"))
                .Returns(new List<ChatChannel> { new ChatChannel { Id = "admin-accessible-channel" } });

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = await provider.CanAccessAsync(admin, "admin-accessible-channel");
            Assert.True(result);
        }

        [Fact]
        public async Task ChatFileAccessProvider_ReturnsFalseForAdmin_WhenChannelNotInAccessibleList()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser("admin"))
                .Returns(new List<ChatChannel> { new ChatChannel { Id = "general-channel" } });

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = await provider.CanAccessAsync(admin, "private-dm-channel");
            Assert.False(result);
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
        public async Task ChatFileAccessProvider_ReturnsFalseForUserNotInChannel()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser("user-1"))
                .Returns(new List<ChatChannel>
                {
                    new ChatChannel { Id = "channel-other" }
                });

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = await provider.CanAccessAsync(user, "target-channel");
            Assert.False(result);
        }

        #endregion

        #region AlbumFileAccessProvider Tests

        [Fact]
        public async Task AlbumFileAccessProvider_ReturnsTrueForChannelMembersAndFalseForNonMembers()
        {
            var mockChatAccess = new Mock<IChatChannelAccessService>();

            mockChatAccess.Setup(s => s.GetUsersForChannel("chan-shared"))
                .Returns(new List<string> { "user-in-channel" });
            mockChatAccess.Setup(s => s.GetUsersForChannel("other-chan"))
                .Returns(new List<string> { "other-user" });

            var provider = new AlbumFileAccessProvider(_albums, mockChatAccess.Object);

            var album = new Album
            {
                Id = "album-1",
                OwnerId = "user-owner",
                ContributorUserIds = new List<string> { "user-contrib" },
                SharedWithChannelIds = new List<string> { "chan-shared" }
            };
            _albums.Save(album);

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
        public async Task AlbumFileAccessProvider_ReturnsFalseWhenAlbumDoesNotExist()
        {
            var mockChatAccess = new Mock<IChatChannelAccessService>();
            var provider = new AlbumFileAccessProvider(_albums, mockChatAccess.Object);
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = await provider.CanAccessAsync(user, "non-existent-album");
            Assert.False(result);
        }

        #endregion

        #region TempFileAccessProvider Tests

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

        #endregion
    }
