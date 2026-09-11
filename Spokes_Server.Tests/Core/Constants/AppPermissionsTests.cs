using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Constants;
using System.Linq;

namespace Spokes_Server.Tests.Core.Constants
{
    public class AppPermissionsTests
    {
        [Fact]
        public void GetAll_ReturnsCompleteDictionary()
        {
            var permissions = AppPermissions.GetAll();

            Assert.NotNull(permissions);
            Assert.Contains("Admin", permissions.Keys);
            Assert.Contains("Projects", permissions.Keys);
            Assert.Contains("Chat", permissions.Keys);
            Assert.Contains("Email", permissions.Keys);
            Assert.Contains("Address Book", permissions.Keys);
            Assert.Contains("Finance", permissions.Keys);
            Assert.Contains("Purchases", permissions.Keys);
            Assert.Contains("Timesheets", permissions.Keys);
            Assert.Contains("My Week", permissions.Keys);
            Assert.Contains("Calendar", permissions.Keys);
            Assert.Contains("Albums", permissions.Keys);

            Assert.Contains(AppPermissions.Admin.ManageUsers, permissions["Admin"]);
            Assert.Contains(AppPermissions.Projects.View, permissions["Projects"]);
            Assert.Contains(AppPermissions.Projects.Delete, permissions["Projects"]);
            Assert.Contains(AppPermissions.Chat.Use, permissions["Chat"]);
            Assert.Contains(AppPermissions.Email.Use, permissions["Email"]);
            Assert.Contains(AppPermissions.AddressBook.View, permissions["Address Book"]);
            Assert.Contains(AppPermissions.AddressBook.Manage, permissions["Address Book"]);
            Assert.Contains(AppPermissions.Finance.AccessExpenses, permissions["Finance"]);
            Assert.Contains(AppPermissions.Purchases.ManagePOs, permissions["Purchases"]);
            Assert.Contains(AppPermissions.Timesheets.AccessTimesheet, permissions["Timesheets"]);
            Assert.Contains(AppPermissions.MyWeek.Edit, permissions["My Week"]);
            Assert.Contains(AppPermissions.Calendar.View, permissions["Calendar"]);
            Assert.Contains(AppPermissions.Albums.View, permissions["Albums"]);
            Assert.Equal("Allows viewing and interacting with albums.", AppPermissions.GetDescription(AppPermissions.Albums.View));
        }
    }
}


