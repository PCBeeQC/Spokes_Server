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
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Tests.Core.Services.Storage;

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

        [Fact]
        public void EmailFileAccessProvider_Category_ReturnsEmail()
        {
            var provider = new EmailFileAccessProvider();
            Assert.Equal("email", provider.Category);
        }


        #endregion

        #region AvatarFileAccessProvider Tests

        [Fact]
        public void AvatarFileAccessProvider_CanAccessAsync_ReturnsTrue()
        {
            var provider = new AvatarFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = provider.CanAccess(user, "any-context-id");
            Assert.True(result);
        }

        #endregion

        #region SignatureFileAccessProvider Tests

        [Fact]
        public void SignatureFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new SignatureFileAccessProvider();
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = provider.CanAccess(admin, "other-user-id");
            Assert.True(result);
        }

        [Fact]
        public void SignatureFileAccessProvider_ReturnsTrueWhenUserMatchesContextId()
        {
            var provider = new SignatureFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = provider.CanAccess(user, "user-1");
            Assert.True(result);
        }

        [Fact]
        public void SignatureFileAccessProvider_ReturnsTrueForFinance()
        {
            var provider = new SignatureFileAccessProvider();
            var financeUser = new Employee
            {
                Id = "finance-1",
                IsAdmin = false,
                Permissions = new List<string> { AppPermissions.Finance.AccessAccounting }
            };

            var result = provider.CanAccess(financeUser, "other-user-id");
            Assert.True(result);
        }

        [Fact]
        public void SignatureFileAccessProvider_ReturnsFalseForUnauthorizedUser()
        {
            var provider = new SignatureFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = new List<string>() };

            var result = provider.CanAccess(user, "other-user-id");
            Assert.False(result);
        }

        #endregion

        #region BillFileAccessProvider Tests

        [Fact]
        public void BillFileAccessProvider_ReturnsTrueForFinance()
        {
            var provider = new BillFileAccessProvider();
            var financeUser = new Employee { Id = "f1", IsAdmin = false, Permissions = new List<string> { AppPermissions.Finance.AccessAccounting } };

            var result = provider.CanAccess(financeUser, "any-bill");
            Assert.True(result);
        }

        [Fact]
        public void BillFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new BillFileAccessProvider();
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = provider.CanAccess(admin, "any-bill");
            Assert.True(result);
        }

        [Theory]
        [InlineData(AppPermissions.Purchases.ViewBills)]
        [InlineData(AppPermissions.Purchases.ManageBills)]
        [InlineData(AppPermissions.Purchases.ApproveBills)]
        [InlineData(AppPermissions.Purchases.ViewPOs)]
        public void BillFileAccessProvider_ReturnsTrueForPurchasesPermissions(string permission)
        {
            var provider = new BillFileAccessProvider();
            var user = new Employee { Id = "purchaser-1", IsAdmin = false, Permissions = new List<string> { permission } };

            var result = provider.CanAccess(user, "any-bill");
            Assert.True(result);
        }

        [Fact]
        public void BillFileAccessProvider_ReturnsFalseForUnauthorizedUser()
        {
            var provider = new BillFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = new List<string>() };

            var result = provider.CanAccess(user, "any-bill");
            Assert.False(result);
        }

        #endregion

        #region RecurringCostFileAccessProvider Tests

        [Fact]
        public void RecurringCostFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new RecurringCostFileAccessProvider();
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = provider.CanAccess(admin, "any-cost");
            Assert.True(result);
        }

        [Fact]
        public void RecurringCostFileAccessProvider_ReturnsTrueForFinance()
        {
            var provider = new RecurringCostFileAccessProvider();
            var financeUser = new Employee
            {
                Id = "finance-1",
                IsAdmin = false,
                Permissions = new List<string> { AppPermissions.Finance.AccessAccounting }
            };

            var result = provider.CanAccess(financeUser, "any-cost");
            Assert.True(result);
        }

        [Fact]
        public void RecurringCostFileAccessProvider_ReturnsFalseForUnauthorizedUser()
        {
            var provider = new RecurringCostFileAccessProvider();
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = new List<string>() };

            var result = provider.CanAccess(user, "any-cost");
            Assert.False(result);
        }

        #endregion

        #region ExpenseFileAccessProvider Tests

        [Fact]
        public void ExpenseFileAccessProvider_ReturnsTrueForOwner()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var user = new Employee { Id = "user-1", IsAdmin = false };
            var report = new ExpenseReport { Id = "e1", EmployeeId = "user-1" };
            _expenseReports.Save(report);

            var result = provider.CanAccess(user, "e1");
            Assert.True(result);
        }

        [Fact]
        public void ExpenseFileAccessProvider_ReturnsFalseForOthers()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var user = new Employee { Id = "user-other", IsAdmin = false };
            var report = new ExpenseReport { Id = "e1", EmployeeId = "user-1" };
            _expenseReports.Save(report);

            var result = provider.CanAccess(user, "e1");
            Assert.False(result);
        }

        [Fact]
        public void ExpenseFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = provider.CanAccess(admin, "non-existent-report");
            Assert.True(result);
        }

        [Fact]
        public void ExpenseFileAccessProvider_ReturnsTrueForFinance()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var financeUser = new Employee
            {
                Id = "finance-1",
                IsAdmin = false,
                Permissions = new List<string> { AppPermissions.Finance.AccessAccounting }
            };

            var result = provider.CanAccess(financeUser, "non-existent-report");
            Assert.True(result);
        }

        [Fact]
        public void ExpenseFileAccessProvider_ReturnsFalseForNonExistentReport()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = new List<string>() };

            var result = provider.CanAccess(user, "non-existent-report");
            Assert.False(result);
        }

        #endregion

        #region ProjectFileAccessProvider Tests

        [Fact]
        public void ProjectFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = provider.CanAccess(admin, "any-project");
            Assert.True(result);
        }

        [Fact]
        public void ProjectFileAccessProvider_ReturnsTrueForAllowedUser()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project { Id = "p1", AllowedUserIds = new List<string> { "user-1" } };
            _projects.Save(project);

            var result = provider.CanAccess(user, "p1");
            Assert.True(result);
        }

        [Fact]
        public void ProjectFileAccessProvider_ReturnsFalseWhenProjectDoesNotExist()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };

            var result = provider.CanAccess(user, "non-existent-project");
            Assert.False(result);
        }

        [Fact]
        public void ProjectFileAccessProvider_ReturnsTrueWhenProjectIsPublic()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project { Id = "p-public", AccessPolicy = ProjectAccessPolicy.Public };
            _projects.Save(project);

            var result = provider.CanAccess(user, "p-public");
            Assert.True(result);
        }

        [Fact]
        public void ProjectFileAccessProvider_ReturnsTrueWhenUserTeamIsAllowed()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", TeamId = "team-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project
            {
                Id = "p-team",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                AllowedTeamIds = new List<string> { "team-1" }
            };
            _projects.Save(project);

            var result = provider.CanAccess(user, "p-team");
            Assert.True(result);
        }

        [Fact]
        public void ProjectFileAccessProvider_ReturnsTrueWhenUserIsLeaderOfAllowedTeam()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var leader = new Employee { Id = "leader-1", TeamId = "team-other", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var team = new Team { Id = "team-1", LeaderId = "leader-1" };
            _teams.Save(team);

            var project = new Project
            {
                Id = "p-team-leader",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                AllowedTeamIds = new List<string> { "team-1" }
            };
            _projects.Save(project);

            var result = provider.CanAccess(leader, "p-team-leader");
            Assert.True(result);
        }

        [Fact]
        public void ProjectFileAccessProvider_ReturnsTrueForProjectManager()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var pm = new Employee { Id = "pm-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project
            {
                Id = "p-pm",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                ProjectManagerId = "pm-1"
            };
            _projects.Save(project);

            var result = provider.CanAccess(pm, "p-pm");
            Assert.True(result);
        }

        [Fact]
        public void ProjectFileAccessProvider_ReturnsFalseWhenUserLacksProjectsViewPermission()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = [] };
            var project = new Project { Id = "p-no-perm", AllowedUserIds = new List<string> { "user-1" } };
            _projects.Save(project);

            var result = provider.CanAccess(user, "p-no-perm");
            Assert.False(result);
        }

        [Fact]
        public void ProjectFileAccessProvider_ReturnsFalseWhenUserIsNotAllowed()
        {
            var provider = new ProjectFileAccessProvider(_projects, _teams);
            var outsider = new Employee { Id = "user-outsider", TeamId = "team-other", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project
            {
                Id = "p-restricted",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                AllowedUserIds = new List<string> { "user-1" },
                AllowedTeamIds = new List<string> { "team-allowed" }
            };
            _projects.Save(project);

            var result = provider.CanAccess(outsider, "p-restricted");
            Assert.False(result);
        }

        #endregion

        #region ProjectNoteFileAccessProvider Tests

        [Fact]
        public void ProjectNoteFileAccessProvider_ReturnsTrueForAdmin()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = provider.CanAccess(admin, "any-project");
            Assert.True(result);
        }

        [Fact]
        public void ProjectNoteFileAccessProvider_ReturnsTrueForPublicProject()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project { Id = "pn-public", AccessPolicy = ProjectAccessPolicy.Public };
            _projects.Save(project);

            var result = provider.CanAccess(user, "pn-public");
            Assert.True(result);
        }

        [Fact]
        public void ProjectNoteFileAccessProvider_ReturnsTrueForAllowedUser()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project { Id = "p1", AllowedUserIds = new List<string> { "user-1" } };
            _projects.Save(project);

            var result = provider.CanAccess(user, "p1");
            Assert.True(result);
        }

        [Fact]
        public void ProjectNoteFileAccessProvider_ReturnsTrueForAllowedTeam()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", TeamId = "team-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project
            {
                Id = "pn-team",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                AllowedTeamIds = new List<string> { "team-1" }
            };
            _projects.Save(project);

            var result = provider.CanAccess(user, "pn-team");
            Assert.True(result);
        }

        [Fact]
        public void ProjectNoteFileAccessProvider_ReturnsTrueForProjectManager()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var pm = new Employee { Id = "pm-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project
            {
                Id = "pn-pm",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                ProjectManagerId = "pm-1"
            };
            _projects.Save(project);

            var result = provider.CanAccess(pm, "pn-pm");
            Assert.True(result);
        }

        [Fact]
        public void ProjectNoteFileAccessProvider_ReturnsFalseWhenUserLacksProjectsViewPermission()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = [] };
            var project = new Project { Id = "pn-no-perm", AllowedUserIds = new List<string> { "user-1" } };
            _projects.Save(project);

            var result = provider.CanAccess(user, "pn-no-perm");
            Assert.False(result);
        }

        [Fact]
        public void ProjectNoteFileAccessProvider_ReturnsFalseForNonExistentProject()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-1", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };

            var result = provider.CanAccess(user, "non-existent-project");
            Assert.False(result);
        }

        [Fact]
        public void ProjectNoteFileAccessProvider_ReturnsFalseForUnauthorizedUser()
        {
            var provider = new ProjectNoteFileAccessProvider(_projects, _teams);
            var user = new Employee { Id = "user-outsider", IsAdmin = false, Permissions = [AppPermissions.Projects.View] };
            var project = new Project
            {
                Id = "pn-restricted",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                AllowedUserIds = new List<string> { "user-1" }
            };
            _projects.Save(project);

            var result = provider.CanAccess(user, "pn-restricted");
            Assert.False(result);
        }

        #endregion

        #region ChatFileAccessProvider Tests

        [Fact]
        public void ChatFileAccessProvider_ReturnsTrueForAdmin_WhenChannelInAccessibleList()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser("admin"))
                .Returns(new List<ChatChannel> { new ChatChannel { Id = "admin-accessible-channel" } });

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = provider.CanAccess(admin, "admin-accessible-channel");
            Assert.True(result);
        }

        [Fact]
        public void ChatFileAccessProvider_ReturnsFalseForAdmin_WhenChannelNotInAccessibleList()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser("admin"))
                .Returns(new List<ChatChannel> { new ChatChannel { Id = "general-channel" } });

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var admin = new Employee { Id = "admin", IsAdmin = true };

            var result = provider.CanAccess(admin, "private-dm-channel");
            Assert.False(result);
        }

        [Fact]
        public void ChatFileAccessProvider_ReturnsTrueForParticipants()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser(It.IsAny<string>()))
                .Returns((string uid) => _channels.GetDirectChannelsForUser(uid).Concat(_channels.GetChannelsForUser(uid, new List<string>(), new List<string>())).ToList());

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var user = new Employee { Id = "user-1" };
            var channel = new ChatChannel { Id = "c1", ParticipantIds = new List<string> { "user-1" }, ChannelType = ChatChannelType.Direct };
            _channels.Save(channel);

            var result = provider.CanAccess(user, "c1");
            Assert.True(result);
        }

        [Fact]
        public void ChatFileAccessProvider_ReturnsTrueForGeneralChannel()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser(It.IsAny<string>()))
                .Returns((string uid) => _channels.GetDirectChannelsForUser(uid).Concat(_channels.GetChannelsForUser(uid, new List<string>(), new List<string>())).ToList());

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var user = new Employee { Id = "user-stranger" };
            var channel = new ChatChannel { Id = "c2", ChannelType = ChatChannelType.General };
            _channels.Save(channel);

            var result = provider.CanAccess(user, "c2");
            Assert.True(result);
        }

        [Fact]
        public void ChatFileAccessProvider_ReturnsFalseForUserNotInChannel()
        {
            var chatServiceMock = new Mock<IChatChannelAccessService>();
            chatServiceMock.Setup(s => s.GetChannelsForUser("user-1"))
                .Returns(new List<ChatChannel>
                {
                    new ChatChannel { Id = "channel-other" }
                });

            var provider = new ChatFileAccessProvider(chatServiceMock.Object);
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = provider.CanAccess(user, "target-channel");
            Assert.False(result);
        }

        #endregion

        #region AlbumFileAccessProvider Tests

        [Fact]
        public void AlbumFileAccessProvider_ReturnsTrueForChannelMembersAndFalseForNonMembers()
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
            Assert.True(provider.CanAccess(new Employee { Id = "user-owner" }, "album-1"));

            // Contributor access
            Assert.True(provider.CanAccess(new Employee { Id = "user-contrib" }, "album-1"));

            // Channel member access
            Assert.True(provider.CanAccess(new Employee { Id = "user-in-channel" }, "album-1"));

            // Non-member access denied
            Assert.False(provider.CanAccess(new Employee { Id = "user-outsider" }, "album-1"));
        }

        [Fact]
        public void AlbumFileAccessProvider_ReturnsFalseWhenAlbumDoesNotExist()
        {
            var mockChatAccess = new Mock<IChatChannelAccessService>();
            var provider = new AlbumFileAccessProvider(_albums, mockChatAccess.Object);
            var user = new Employee { Id = "user-1", IsAdmin = false };

            var result = provider.CanAccess(user, "non-existent-album");
            Assert.False(result);
        }

        [Fact]
        public void AlbumFileAccessProvider_CanUpload_ReturnsTrueForOwnerAndContributor_FalseForChannelMembersAndOutsiders()
        {
            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(s => s.GetUsersForChannel("chan-shared"))
                .Returns(new List<string> { "user-in-channel" });

            var provider = new AlbumFileAccessProvider(_albums, mockChatAccess.Object);

            var album = new Album
            {
                Id = "album-upload-test",
                OwnerId = "user-owner",
                ContributorUserIds = new List<string> { "user-contrib" },
                SharedWithChannelIds = new List<string> { "chan-shared" }
            };
            _albums.Save(album);

            // Owner can upload
            Assert.True(provider.CanUpload(new Employee { Id = "user-owner" }, "album-upload-test"));

            // Contributor can upload
            Assert.True(provider.CanUpload(new Employee { Id = "user-contrib" }, "album-upload-test"));

            // Channel member CANNOT upload (read-only access)
            Assert.False(provider.CanUpload(new Employee { Id = "user-in-channel" }, "album-upload-test"));

            // Outsider CANNOT upload
            Assert.False(provider.CanUpload(new Employee { Id = "user-outsider" }, "album-upload-test"));

            // Non-existent album
            Assert.False(provider.CanUpload(new Employee { Id = "user-owner" }, "non-existent-album"));
        }

        #endregion

        #region TempFileAccessProvider Tests

        [Fact]
        public void TempFileAccessProvider_CanAccess_AlwaysReturnsFalse()
        {
            var provider = new TempFileAccessProvider();
            var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true, IsSuspended = false, IsBanned = false };
            var employee = new Employee
            {
                Id = "emp1",
                IsActive = true,
                Permissions = new List<string> { AppPermissions.Projects.Edit }
            };

            Assert.False(provider.CanAccess(admin, Guid.NewGuid().ToString()));
            Assert.False(provider.CanAccess(employee, Guid.NewGuid().ToString()));
            Assert.False(provider.CanAccess(null!, Guid.NewGuid().ToString()));
        }

        [Theory]
        [InlineData(AppPermissions.Admin.ManageSettings)]
        [InlineData(AppPermissions.Projects.Create)]
        [InlineData(AppPermissions.Projects.Edit)]
        [InlineData(AppPermissions.BusinessDocuments.View)]
        public void TempFileAccessProvider_CanUpload_ReturnsTrueForAuthorizedPermission(string permission)
        {
            var provider = new TempFileAccessProvider();
            var employee = new Employee
            {
                Id = "emp1",
                IsActive = true,
                IsSuspended = false,
                IsBanned = false,
                Permissions = new List<string> { permission }
            };

            Assert.True(provider.CanUpload(employee, "ctx-1"));
        }

        [Fact]
        public void TempFileAccessProvider_CanUpload_ReturnsTrueForAdmin_AndFalseWithoutEligiblePermission()
        {
            var provider = new TempFileAccessProvider();
            var admin = new Employee { Id = "admin1", IsAdmin = true, IsActive = true };
            var unprivileged = new Employee { Id = "emp1", IsActive = true, Permissions = new List<string> { AppPermissions.Chat.Use } };

            Assert.True(provider.CanUpload(admin, "ctx-1"));
            Assert.False(provider.CanUpload(unprivileged, "ctx-1"));
            Assert.False(provider.CanUpload(null!, "ctx-1"));
        }

        [Theory]
        [InlineData(false, false, false)] // Inactive
        [InlineData(true, true, false)]   // Suspended
        [InlineData(true, false, true)]   // Banned
        public void TempFileAccessProvider_CanUpload_ReturnsFalseForInactiveSuspendedOrBannedEmployee(bool isActive, bool isSuspended, bool isBanned)
        {
            var provider = new TempFileAccessProvider();
            var employee = new Employee
            {
                Id = "emp1",
                IsAdmin = true,
                IsActive = isActive,
                IsSuspended = isSuspended,
                IsBanned = isBanned
            };

            Assert.False(provider.CanAccess(employee, Guid.NewGuid().ToString()));
            Assert.False(provider.CanUpload(employee, Guid.NewGuid().ToString()));
        }

        #endregion

        #region EmailFileAccessProvider Tests

        [Fact]
        public void EmailFileAccessProvider_RequiresAccountAndMessageScopingForNonBillInbox_AndNeverAllowsUploads()
        {
            var provider = new EmailFileAccessProvider();
            var employee = new Employee { Id = "emp1", IsActive = true, IsSuspended = false, IsBanned = false, IsAdmin = false };

            // Bare accountId without ":messageId" must be rejected for non-bill-inbox reads
            Assert.False(provider.CanAccess(employee, "emp1"));
            Assert.False(provider.CanAccess(employee, "emp1:"));
            Assert.False(provider.CanAccess(employee, ":msg1"));

            // Scoped "accountId:messageId" succeeds for owner, while CanUpload is always false
            Assert.True(provider.CanAccess(employee, "emp1:msg1"));
            Assert.False(provider.CanUpload(employee, "emp1:msg1"));
            Assert.False(provider.CanUpload(employee, "emp1"));
        }

        [Fact]
        public void EmailFileAccessProvider_ReturnsTrueForAdminWithScopedContext_AndFalseForUpload()
        {
            var provider = new EmailFileAccessProvider();
            var admin = new Employee { Id = "admin1", IsActive = true, IsSuspended = false, IsBanned = false, IsAdmin = true };

            Assert.False(provider.CanAccess(admin, "emp2"));
            Assert.True(provider.CanAccess(admin, "emp2:msg42"));
            Assert.False(provider.CanUpload(admin, "emp2:msg42"));
        }

        [Fact]
        public void EmailFileAccessProvider_ReturnsFalseForOtherEmployee()
        {
            var provider = new EmailFileAccessProvider();
            var employee = new Employee { Id = "emp1", IsActive = true, IsSuspended = false, IsBanned = false, IsAdmin = false };

            Assert.False(provider.CanAccess(employee, "emp2:msg1"));
            Assert.False(provider.CanUpload(employee, "emp2:msg1"));
        }

        [Fact]
        public void EmailFileAccessProvider_DelegatesScopedMessageCheckToEmailInboxService()
        {
            var mockInboxService = new Mock<IEmailInboxService>();
            var employee = new Employee { Id = "emp_vi", IsActive = true };
            mockInboxService.Setup(s => s.CanUserAccessEmailMessage(employee, "acct_shared", "msg_allowed")).Returns(true);
            mockInboxService.Setup(s => s.CanUserAccessEmailMessage(employee, "acct_shared", "msg_other_vi")).Returns(false);

            var provider = new EmailFileAccessProvider(mockInboxService.Object);

            Assert.False(provider.CanAccess(employee, "acct_shared"));
            Assert.True(provider.CanAccess(employee, "acct_shared:msg_allowed"));
            Assert.False(provider.CanAccess(employee, "acct_shared:msg_other_vi"));
        }

        [Theory]
        [InlineData(false, false, false)] // Inactive
        [InlineData(true, false, true)]   // Banned
        public void EmailFileAccessProvider_ReturnsFalseForInactiveOrBannedEmployee(bool isActive, bool isSuspended, bool isBanned)
        {
            var provider = new EmailFileAccessProvider();
            var employee = new Employee { Id = "emp1", IsActive = isActive, IsSuspended = isSuspended, IsBanned = isBanned };

            Assert.False(provider.CanAccess(employee, "emp1:msg1"));
            Assert.False(provider.CanUpload(employee, "emp1:msg1"));
        }

        [Fact]
        public void EmailFileAccessProvider_AllowsReadOnlyForSuspendedEmployee()
        {
            var provider = new EmailFileAccessProvider();
            var employee = new Employee { Id = "emp1", IsActive = true, IsSuspended = true, IsBanned = false };

            Assert.True(provider.CanAccess(employee, "emp1:msg1"));
            Assert.False(provider.CanUpload(employee, "emp1:msg1"));
        }

        [Fact]
        public void EmailFileAccessProvider_ReturnsFalseForNullUserOrContext()
        {
            var provider = new EmailFileAccessProvider();
            var employee = new Employee { Id = "emp1", IsActive = true };

            Assert.False(provider.CanAccess(null!, "emp1:msg1"));
            Assert.False(provider.CanAccess(employee, null!));
            Assert.False(provider.CanAccess(employee, ""));
            Assert.False(provider.CanAccess(employee, "   "));
        }

        [Theory]
        [InlineData(AppPermissions.Finance.AccessAccounting)]
        [InlineData(AppPermissions.Purchases.ViewBills)]
        [InlineData(AppPermissions.Purchases.ManageBills)]
        [InlineData(AppPermissions.Purchases.ApproveBills)]
        public void EmailFileAccessProvider_ReturnsTrueForAccountingPermissions_WhenContextIsSystemEmployeeId(string permission)
        {
            var provider = new EmailFileAccessProvider();
            var employee = new Employee
            {
                Id = "acct-user",
                IsActive = true,
                IsSuspended = false,
                IsBanned = false,
                IsAdmin = false,
                Permissions = new List<string> { permission }
            };

            Assert.True(provider.CanAccess(employee, CompanyBillInboxConstants.SystemEmployeeId));
            Assert.True(provider.CanAccess(employee, $"{CompanyBillInboxConstants.SystemEmployeeId}:bill_msg_1"));
            Assert.False(provider.CanUpload(employee, CompanyBillInboxConstants.SystemEmployeeId));
        }

        [Fact]
        public void EmailFileAccessProvider_ReturnsFalseForAccountingPermissions_WhenContextIsNotSystemEmployeeId()
        {
            var provider = new EmailFileAccessProvider();
            var employee = new Employee
            {
                Id = "acct-user",
                IsActive = true,
                IsSuspended = false,
                IsBanned = false,
                IsAdmin = false,
                Permissions = new List<string> { AppPermissions.Finance.AccessAccounting }
            };

            // Cannot access another employee's private email attachments
            Assert.False(provider.CanAccess(employee, "other-emp:msg1"));
            Assert.False(provider.CanUpload(employee, "other-emp:msg1"));
        }

        [Fact]
        public void EmailFileAccessProvider_ReturnsFalseForRegularEmployeeWithoutPermissions_WhenContextIsSystemEmployeeId()
        {
            var provider = new EmailFileAccessProvider();
            var employee = new Employee
            {
                Id = "regular-user",
                IsActive = true,
                IsSuspended = false,
                IsBanned = false,
                IsAdmin = false,
                Permissions = new List<string>()
            };

            Assert.False(provider.CanAccess(employee, CompanyBillInboxConstants.SystemEmployeeId));
            Assert.False(provider.CanUpload(employee, CompanyBillInboxConstants.SystemEmployeeId));
        }

        #endregion

        #region Status Guard & Read/Upload Separation Tests Across Providers

        [Theory]
        [InlineData(false, false, false)] // Inactive
        [InlineData(true, false, true)]   // Banned
        public void AllProviders_DenyInactiveOrBannedUsersForBothReadAndUpload(bool isActive, bool isSuspended, bool isBanned)
        {
            _albums.Save(new Album { Id = "alb-status", OwnerId = "u1" });
            _projects.Save(new Project { Id = "proj-status", AccessPolicy = ProjectAccessPolicy.Public });
            _expenseReports.Save(new ExpenseReport { Id = "exp-status", EmployeeId = "u1" });
            _channels.Save(new ChatChannel { Id = "chan-status", ChannelType = ChatChannelType.General });

            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(s => s.GetChannelsForUser("u1")).Returns(new List<ChatChannel> { new() { Id = "chan-status" } });

            var user = new Employee { Id = "u1", IsAdmin = true, IsActive = isActive, IsSuspended = isSuspended, IsBanned = isBanned };

            var avatar = new AvatarFileAccessProvider();
            var sig = new SignatureFileAccessProvider();
            var expense = new ExpenseFileAccessProvider(_expenseReports);
            var bill = new BillFileAccessProvider();
            var recurring = new RecurringCostFileAccessProvider();
            var album = new AlbumFileAccessProvider(_albums, mockChatAccess.Object);
            var chat = new ChatFileAccessProvider(mockChatAccess.Object, _channels);
            var proj = new ProjectFileAccessProvider(_projects, _teams);
            var note = new ProjectNoteFileAccessProvider(_projects, _teams);

            Assert.False(avatar.CanAccess(user, "u1"));
            Assert.False(avatar.CanUpload(user, "u1"));
            Assert.False(sig.CanAccess(user, "u1"));
            Assert.False(sig.CanUpload(user, "u1"));
            Assert.False(expense.CanAccess(user, "exp-status"));
            Assert.False(expense.CanUpload(user, "exp-status"));
            Assert.False(bill.CanAccess(user, "any-bill"));
            Assert.False(bill.CanUpload(user, "any-bill"));
            Assert.False(recurring.CanAccess(user, "any-cost"));
            Assert.False(recurring.CanUpload(user, "any-cost"));
            Assert.False(album.CanAccess(user, "alb-status"));
            Assert.False(album.CanUpload(user, "alb-status"));
            Assert.False(chat.CanAccess(user, "chan-status"));
            Assert.False(chat.CanUpload(user, "chan-status"));
            Assert.False(proj.CanAccess(user, "proj-status"));
            Assert.False(proj.CanUpload(user, "proj-status"));
            Assert.False(note.CanAccess(user, "proj-status"));
            Assert.False(note.CanUpload(user, "proj-status"));
        }

        [Fact]
        public void AllReadableProviders_AllowSuspendedUserForRead_AndDenySuspendedUserForUpload()
        {
            _albums.Save(new Album { Id = "alb-susp", OwnerId = "u_susp" });
            _projects.Save(new Project { Id = "proj-susp", AccessPolicy = ProjectAccessPolicy.Public });
            _expenseReports.Save(new ExpenseReport { Id = "exp-susp", EmployeeId = "u_susp" });
            _channels.Save(new ChatChannel { Id = "chan-susp", ChannelType = ChatChannelType.General });

            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(s => s.GetChannelsForUser("u_susp")).Returns(new List<ChatChannel> { new() { Id = "chan-susp" } });

            var suspendedUser = new Employee
            {
                Id = "u_susp",
                IsActive = true,
                IsSuspended = true,
                IsBanned = false,
                Permissions = new List<string>
                {
                    AppPermissions.Projects.View,
                    AppPermissions.Projects.Edit,
                    AppPermissions.Finance.AccessAccounting,
                    AppPermissions.Finance.AccessExpenses,
                    AppPermissions.Purchases.ManageBills
                }
            };

            var avatar = new AvatarFileAccessProvider();
            var sig = new SignatureFileAccessProvider();
            var expense = new ExpenseFileAccessProvider(_expenseReports);
            var bill = new BillFileAccessProvider();
            var recurring = new RecurringCostFileAccessProvider();
            var album = new AlbumFileAccessProvider(_albums, mockChatAccess.Object);
            var chat = new ChatFileAccessProvider(mockChatAccess.Object, _channels);
            var proj = new ProjectFileAccessProvider(_projects, _teams);
            var note = new ProjectNoteFileAccessProvider(_projects, _teams);

            Assert.True(avatar.CanAccess(suspendedUser, "u_susp"));
            Assert.False(avatar.CanUpload(suspendedUser, "u_susp"));

            Assert.True(sig.CanAccess(suspendedUser, "u_susp"));
            Assert.False(sig.CanUpload(suspendedUser, "u_susp"));

            Assert.True(expense.CanAccess(suspendedUser, "exp-susp"));
            Assert.False(expense.CanUpload(suspendedUser, "exp-susp"));

            Assert.True(bill.CanAccess(suspendedUser, "any-bill"));
            Assert.False(bill.CanUpload(suspendedUser, "any-bill"));

            Assert.True(recurring.CanAccess(suspendedUser, "any-cost"));
            Assert.False(recurring.CanUpload(suspendedUser, "any-cost"));

            Assert.True(album.CanAccess(suspendedUser, "alb-susp"));
            Assert.False(album.CanUpload(suspendedUser, "alb-susp"));

            Assert.True(chat.CanAccess(suspendedUser, "chan-susp"));
            Assert.False(chat.CanUpload(suspendedUser, "chan-susp"));

            Assert.True(proj.CanAccess(suspendedUser, "proj-susp"));
            Assert.False(proj.CanUpload(suspendedUser, "proj-susp"));

            Assert.True(note.CanAccess(suspendedUser, "proj-susp"));
            Assert.False(note.CanUpload(suspendedUser, "proj-susp"));
        }

        [Fact]
        public void ChatFileAccessProvider_CanUpload_EnforcesChannelPostPermissionAndBlocksArchivedOrAnnouncementReadOnly()
        {
            var normalChannel = new ChatChannel { Id = "c-normal", ChannelType = ChatChannelType.General };
            var archivedChannel = new ChatChannel { Id = "c-archived", ChannelType = ChatChannelType.General, IsArchived = true };
            var announceChannel = new ChatChannel
            {
                Id = "c-announce",
                ChannelType = ChatChannelType.General,
                IsAnnouncementOnly = true,
                AllowedPostUserIds = new List<string> { "u-poster" }
            };
            _channels.Save(normalChannel);
            _channels.Save(archivedChannel);
            _channels.Save(announceChannel);

            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(s => s.GetChannelsForUser(It.IsAny<string>()))
                .Returns(new List<ChatChannel> { normalChannel, archivedChannel, announceChannel });

            var provider = new ChatFileAccessProvider(mockChatAccess.Object, _channels);
            var regularMember = new Employee { Id = "u-reader", IsActive = true };
            var allowedPoster = new Employee { Id = "u-poster", IsActive = true };

            Assert.True(provider.CanUpload(regularMember, "c-normal"));
            Assert.False(provider.CanUpload(regularMember, "c-archived"));
            Assert.False(provider.CanUpload(regularMember, "c-announce"));
            Assert.True(provider.CanUpload(allowedPoster, "c-announce"));
        }

        [Fact]
        public void BillFileAccessProvider_CanUpload_AllowsOnlyAccountingManageBillsOrAdmin_DeniesReadOnlyViewers()
        {
            var provider = new BillFileAccessProvider();

            Assert.True(provider.CanUpload(new Employee { Id = "u1", IsActive = true, IsAdmin = true }, "b1"));
            Assert.True(provider.CanUpload(new Employee { Id = "u2", IsActive = true, Permissions = [AppPermissions.Finance.AccessAccounting] }, "b1"));
            Assert.True(provider.CanUpload(new Employee { Id = "u3", IsActive = true, Permissions = [AppPermissions.Purchases.ManageBills] }, "b1"));

            Assert.False(provider.CanUpload(new Employee { Id = "u4", IsActive = true, Permissions = [AppPermissions.Purchases.ViewBills] }, "b1"));
            Assert.False(provider.CanUpload(new Employee { Id = "u5", IsActive = true, Permissions = [AppPermissions.Purchases.ApproveBills] }, "b1"));
            Assert.False(provider.CanUpload(new Employee { Id = "u6", IsActive = true, Permissions = [AppPermissions.Purchases.ViewPOs] }, "b1"));
        }

        [Fact]
        public void ExpenseFileAccessProvider_CanUpload_RequiresDraftReportForOwnerWithAccessExpenses_OrAccounting()
        {
            var provider = new ExpenseFileAccessProvider(_expenseReports);
            _expenseReports.Save(new ExpenseReport { Id = "exp-draft", EmployeeId = "u-owner" });
            _expenseReports.Save(new ExpenseReport { Id = "exp-submitted", EmployeeId = "u-owner", DateSubmitted = DateTime.UtcNow });

            var ownerWithPerm = new Employee { Id = "u-owner", IsActive = true, Permissions = [AppPermissions.Finance.AccessExpenses] };
            var ownerWithoutPerm = new Employee { Id = "u-owner", IsActive = true, Permissions = [] };
            var otherWithPerm = new Employee { Id = "u-other", IsActive = true, Permissions = [AppPermissions.Finance.AccessExpenses] };
            var financeUser = new Employee { Id = "u-fin", IsActive = true, Permissions = [AppPermissions.Finance.AccessAccounting] };

            Assert.True(provider.CanUpload(ownerWithPerm, "exp-draft"));
            Assert.False(provider.CanUpload(ownerWithPerm, "exp-submitted"));
            Assert.False(provider.CanUpload(ownerWithoutPerm, "exp-draft"));
            Assert.False(provider.CanUpload(otherWithPerm, "exp-draft"));
            Assert.False(provider.CanUpload(ownerWithPerm, "exp-missing"));

            Assert.True(provider.CanUpload(financeUser, "exp-submitted"));
            Assert.False(provider.CanUpload(financeUser, "exp-missing"));
        }

        [Fact]
        public void AvatarSignatureAndProjectProviders_CanUpload_EnforceWriteAuthorization()
        {
            var avatar = new AvatarFileAccessProvider();
            var sig = new SignatureFileAccessProvider();
            var proj = new ProjectFileAccessProvider(_projects, _teams);
            var note = new ProjectNoteFileAccessProvider(_projects, _teams);

            _projects.Save(new Project { Id = "p-rw", AccessPolicy = ProjectAccessPolicy.Public });

            var user = new Employee { Id = "u1", IsActive = true, Permissions = [AppPermissions.Projects.View, AppPermissions.Finance.AccessAccounting] };
            var editor = new Employee { Id = "u2", IsActive = true, Permissions = [AppPermissions.Projects.View, AppPermissions.Projects.Edit] };

            Assert.True(avatar.CanUpload(user, "u1"));
            Assert.False(avatar.CanUpload(user, "u2"));

            Assert.True(sig.CanUpload(user, "u1"));
            // Finance user can READ u2's signature for PDFs, but CANNOT upload/overwrite u2's signature
            Assert.True(sig.CanAccess(user, "u2"));
            Assert.False(sig.CanUpload(user, "u2"));

            Assert.False(proj.CanUpload(user, "p-rw"));
            Assert.True(proj.CanUpload(editor, "p-rw"));
            Assert.False(note.CanUpload(user, "p-rw"));
            Assert.True(note.CanUpload(editor, "p-rw"));
        }

        #endregion
    }


