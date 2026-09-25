using Spokes_Server.Core.Constants;

namespace Spokes_Server.Tests.Core.Constants
{
    public class DefaultPermissionGroupsTests
    {
        [Fact]
        public void GetBusinessDefaults_ContainsAlbumsViewInAllRoles()
        {
            var groups = DefaultPermissionGroups.GetBusinessDefaults();
            Assert.NotEmpty(groups);

            var employee = groups.FirstOrDefault(g => g.Id == "business_employee");
            var manager = groups.FirstOrDefault(g => g.Id == "business_manager");
            var director = groups.FirstOrDefault(g => g.Id == "business_director");

            Assert.NotNull(employee);
            Assert.NotNull(manager);
            Assert.NotNull(director);

            Assert.Contains(AppPermissions.Albums.View, employee.Permissions);
            Assert.Contains(AppPermissions.Albums.View, manager.Permissions);
            Assert.Contains(AppPermissions.Albums.View, director.Permissions);
        }

        [Fact]
        public void GetFriendDefaults_ContainsAlbumsViewInAllRoles()
        {
            var groups = DefaultPermissionGroups.GetFriendDefaults();
            Assert.NotEmpty(groups);

            var member = groups.FirstOrDefault(g => g.Id == "friend_member");
            var admin = groups.FirstOrDefault(g => g.Id == "friend_admin");

            Assert.NotNull(member);
            Assert.NotNull(admin);

            Assert.Contains(AppPermissions.Albums.View, member.Permissions);
            Assert.Contains(AppPermissions.Albums.View, admin.Permissions);
        }

        [Fact]
        public void GetBusinessDefaults_ReturnsThreeDistinctHierarchicalGroups()
        {
            var groups = DefaultPermissionGroups.GetBusinessDefaults();

            Assert.Equal(3, groups.Count);

            var employee = groups.FirstOrDefault(g => g.Id == "business_employee");
            var manager = groups.FirstOrDefault(g => g.Id == "business_manager");
            var director = groups.FirstOrDefault(g => g.Id == "business_director");

            Assert.NotNull(employee);
            Assert.Equal("Employee", employee.Name);
            Assert.Contains(AppPermissions.Chat.Use, employee.Permissions);
            Assert.Contains(AppPermissions.Email.Use, employee.Permissions);
            Assert.Contains(AppPermissions.AddressBook.View, employee.Permissions);
            Assert.Contains(AppPermissions.Timesheets.AccessTimesheet, employee.Permissions);
            Assert.Contains(AppPermissions.Finance.AccessExpenses, employee.Permissions);
            Assert.Contains(AppPermissions.Calendar.View, employee.Permissions);
            Assert.DoesNotContain(AppPermissions.Projects.Create, employee.Permissions);
            Assert.DoesNotContain(AppPermissions.Admin.ManageUsers, employee.Permissions);

            Assert.NotNull(manager);
            Assert.Equal("Manager", manager.Name);
            Assert.Contains(AppPermissions.Projects.View, manager.Permissions);
            Assert.Contains(AppPermissions.Projects.Create, manager.Permissions);
            Assert.Contains(AppPermissions.Projects.Edit, manager.Permissions);
            Assert.Contains(AppPermissions.Purchases.ViewPOs, manager.Permissions);
            Assert.Contains(AppPermissions.Purchases.ManagePOs, manager.Permissions);
            Assert.Contains(AppPermissions.Chat.Moderator, manager.Permissions);
            Assert.DoesNotContain(AppPermissions.Finance.AccessAccounting, manager.Permissions);
            Assert.DoesNotContain(AppPermissions.Admin.ManageUsers, manager.Permissions);

            Assert.NotNull(director);
            Assert.Equal("Director", director.Name);
            Assert.Contains(AppPermissions.Finance.AccessAccounting, director.Permissions);
            Assert.Contains(AppPermissions.Purchases.ViewBills, director.Permissions);
            Assert.Contains(AppPermissions.Purchases.ManageBills, director.Permissions);
            Assert.Contains(AppPermissions.Timesheets.ViewReport, director.Permissions);
            Assert.Contains(AppPermissions.Admin.ManageUsers, director.Permissions);
            Assert.Contains(AppPermissions.Chat.EditAllPublicChannels, director.Permissions);
        }

        [Fact]
        public void GetFriendDefaults_ReturnsMemberAndAdminGroups()
        {
            var groups = DefaultPermissionGroups.GetFriendDefaults();

            Assert.Equal(2, groups.Count);

            var member = groups.FirstOrDefault(g => g.Id == "friend_member");
            var admin = groups.FirstOrDefault(g => g.Id == "friend_admin");

            Assert.NotNull(member);
            Assert.Equal("Member", member.Name);
            Assert.Equal(3, member.Permissions.Count);
            Assert.Contains(AppPermissions.Chat.Use, member.Permissions);
            Assert.Contains(AppPermissions.Calendar.View, member.Permissions);
            Assert.Contains(AppPermissions.Albums.View, member.Permissions);
            Assert.DoesNotContain(AppPermissions.Chat.Moderator, member.Permissions);
            Assert.DoesNotContain(AppPermissions.Chat.CreateChannels, member.Permissions);

            Assert.NotNull(admin);
            Assert.Equal("Administrator", admin.Name);
            Assert.Equal(7, admin.Permissions.Count);
            Assert.Contains(AppPermissions.Chat.Use, admin.Permissions);
            Assert.Contains(AppPermissions.Calendar.View, admin.Permissions);
            Assert.Contains(AppPermissions.Albums.View, admin.Permissions);
            Assert.Contains(AppPermissions.Chat.CreateChannels, admin.Permissions);
            Assert.Contains(AppPermissions.Chat.ViewArchive, admin.Permissions);
            Assert.Contains(AppPermissions.Chat.EditAllPublicChannels, admin.Permissions);
            Assert.Contains(AppPermissions.Chat.Moderator, admin.Permissions);
        }

        [Fact]
        public void GetBusinessDefaults_And_GetFriendDefaults_ReturnsNewInstancesEachTime()
        {
            var business1 = DefaultPermissionGroups.GetBusinessDefaults();
            var business2 = DefaultPermissionGroups.GetBusinessDefaults();

            Assert.NotSame(business1, business2);
            Assert.NotSame(business1[0], business2[0]);
            Assert.NotSame(business1[0].Permissions, business2[0].Permissions);

            var friend1 = DefaultPermissionGroups.GetFriendDefaults();
            var friend2 = DefaultPermissionGroups.GetFriendDefaults();

            Assert.NotSame(friend1, friend2);
            Assert.NotSame(friend1[0], friend2[0]);
            Assert.NotSame(friend1[0].Permissions, friend2[0].Permissions);
        }
    }
}
