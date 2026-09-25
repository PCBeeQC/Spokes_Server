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

        [Fact]
        public void GetAll_ContainsAllExpectedPermissionCategories_AndPermissionsAreNonEmpty()
        {
            var expectedCategories = new[]
            {
                "Admin",
                "Projects",
                "Chat",
                "Email",
                "Address Book",
                "Finance",
                "Purchases",
                "Timesheets",
                "Business Documents",
                "Albums",
                "My Week",
                "Calendar"
            };

            var permissions = AppPermissions.GetAll();

            Assert.NotNull(permissions);
            Assert.Equal(expectedCategories.Length, permissions.Count);

            foreach (var category in expectedCategories)
            {
                Assert.True(permissions.ContainsKey(category), $"Missing category: {category}");
                Assert.NotEmpty(permissions[category]);
            }

            var allPermissions = permissions.Values.SelectMany(p => p).ToList();
            Assert.Equal(allPermissions.Distinct().Count(), allPermissions.Count);
            Assert.All(allPermissions, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        }

        [Theory]
        [InlineData(AppPermissions.Admin.ManageUsers, "Full admin access to create, edit, and modify users.")]
        [InlineData(AppPermissions.Admin.ManageSettings, "Access to system-wide settings and configurations.")]
        [InlineData(AppPermissions.Projects.View, "Allows viewing projects.")]
        [InlineData(AppPermissions.Projects.Create, "Allows creating new projects.")]
        [InlineData(AppPermissions.Projects.Edit, "Allows editing existing project details.")]
        [InlineData(AppPermissions.Projects.Delete, "Allows deleting projects.")]
        [InlineData(AppPermissions.Chat.Use, "Allows using the team chat.")]
        [InlineData(AppPermissions.Chat.CreateChannels, "Allows creating new chat channels.")]
        [InlineData(AppPermissions.Chat.ViewArchive, "Allows viewing archived chat channels.")]
        [InlineData(AppPermissions.Chat.EditAllPublicChannels, "Allows editing all public channels, even if not the creator.")]
        [InlineData(AppPermissions.Chat.Moderator, "Allows moderating chat channels (e.g. deleting reported messages).")]
        [InlineData(AppPermissions.Email.Use, "Allows sending and receiving emails.")]
        [InlineData(AppPermissions.AddressBook.View, "Allows viewing the address book.")]
        [InlineData(AppPermissions.AddressBook.Manage, "Allows adding, editing, and deleting contacts in the address book.")]
        [InlineData(AppPermissions.Finance.AccessAccounting, "Provides access to the accounting dashboard.")]
        [InlineData(AppPermissions.Finance.AccessExpenses, "Allows accessing expense reports.")]
        [InlineData(AppPermissions.Finance.TeamCapacity, "Allows viewing team capacity reports.")]
        [InlineData(AppPermissions.Purchases.ViewPOs, "Allows viewing purchase orders.")]
        [InlineData(AppPermissions.Purchases.ManagePOs, "Allows creating, editing, and deleting purchase orders.")]
        [InlineData(AppPermissions.Purchases.ViewBills, "Allows viewing bills.")]
        [InlineData(AppPermissions.Purchases.ManageBills, "Allows creating, editing, and deleting bills.")]
        [InlineData(AppPermissions.Purchases.ApproveBills, "Allows reviewing and approving bills for payment.")]
        [InlineData(AppPermissions.Timesheets.AccessTimesheet, "Allows accessing personal timesheets.")]
        [InlineData(AppPermissions.Timesheets.ViewReport, "Allows viewing timesheet reports for the team.")]
        [InlineData(AppPermissions.BusinessDocuments.View, "Allows accessing and managing business documents.")]
        [InlineData(AppPermissions.Albums.View, "Allows viewing and interacting with albums.")]
        [InlineData(AppPermissions.MyWeek.Edit, "Allows editing the My Week view.")]
        [InlineData(AppPermissions.Calendar.View, "Allows accessing the calendar.")]
        public void GetDescription_KnownPermissions_ReturnsExpectedDescription(string permission, string expectedDescription)
        {
            var description = AppPermissions.GetDescription(permission);
            Assert.Equal(expectedDescription, description);
        }

        [Theory]
        [InlineData("Unknown.Permission")]
        [InlineData("Admin.NonExistent")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("random_string")]
        public void GetDescription_UnknownPermission_ReturnsUnknownPermissionFallback(string? permission)
        {
            var description = AppPermissions.GetDescription(permission!);
            Assert.Equal("Unknown permission.", description);
        }

        [Fact]
        public void GetAll_AllPermissionsHaveMappedDescriptions()
        {
            var allPermissions = AppPermissions.GetAll().Values.SelectMany(p => p);

            Assert.All(allPermissions, permission =>
            {
                var description = AppPermissions.GetDescription(permission);
                Assert.NotEqual("Unknown permission.", description);
                Assert.False(string.IsNullOrWhiteSpace(description));
            });
        }
    }
}
