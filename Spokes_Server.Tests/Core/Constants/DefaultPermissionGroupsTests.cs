using Spokes_Server.Core.Constants;
using System.Linq;
using Xunit;

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
    }
}
