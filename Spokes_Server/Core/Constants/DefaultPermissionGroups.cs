namespace Spokes_Server.Core.Constants;

using Spokes_Server.Core.Models.Core;

public static class DefaultPermissionGroups
{
    public static List<PermissionGroup> GetBusinessDefaults()
    {
        return new List<PermissionGroup>
        {
            new PermissionGroup
            {
                Id = "business_employee",
                Name = "Employee",
                Permissions = new List<string>
                {
                    AppPermissions.Chat.Use,
                    AppPermissions.Email.Use,
                    AppPermissions.AddressBook.View,
                    AppPermissions.Timesheets.AccessTimesheet,
                    AppPermissions.Finance.AccessExpenses,
                    AppPermissions.Calendar.View,
                    AppPermissions.Albums.View
                }
            },
            new PermissionGroup
            {
                Id = "business_manager",
                Name = "Manager",
                Permissions = new List<string>
                {
                    AppPermissions.Chat.Use,
                    AppPermissions.Email.Use,
                    AppPermissions.AddressBook.View,
                    AppPermissions.Timesheets.AccessTimesheet,
                    AppPermissions.Finance.AccessExpenses,
                    AppPermissions.Calendar.View,
                    AppPermissions.AddressBook.Manage,
                    AppPermissions.Projects.View,
                    AppPermissions.Projects.Create,
                    AppPermissions.Projects.Edit,
                    AppPermissions.Chat.CreateChannels,
                    AppPermissions.Purchases.ViewPOs,
                    AppPermissions.Purchases.ManagePOs,
                    AppPermissions.MyWeek.Edit,
                    AppPermissions.BusinessDocuments.View,
                    AppPermissions.Chat.Moderator,
                    AppPermissions.Albums.View
                }
            },
            new PermissionGroup
            {
                Id = "business_director",
                Name = "Director",
                Permissions = new List<string>
                {
                    AppPermissions.Chat.Use,
                    AppPermissions.Email.Use,
                    AppPermissions.AddressBook.View,
                    AppPermissions.Timesheets.AccessTimesheet,
                    AppPermissions.Finance.AccessExpenses,
                    AppPermissions.Calendar.View,
                    AppPermissions.AddressBook.Manage,
                    AppPermissions.Projects.View,
                    AppPermissions.Projects.Create,
                    AppPermissions.Projects.Edit,
                    AppPermissions.Chat.CreateChannels,
                    AppPermissions.Purchases.ViewPOs,
                    AppPermissions.Purchases.ManagePOs,
                    AppPermissions.MyWeek.Edit,
                    AppPermissions.BusinessDocuments.View,
                    AppPermissions.Chat.ViewArchive,
                    AppPermissions.Finance.AccessAccounting,
                    AppPermissions.Purchases.ViewBills,
                    AppPermissions.Purchases.ManageBills,
                    AppPermissions.Timesheets.ViewReport,
                    AppPermissions.Admin.ManageUsers,
                    AppPermissions.Chat.EditAllPublicChannels,
                    AppPermissions.Chat.Moderator,
                    AppPermissions.Albums.View
                }
            }
        };
    }

    public static List<PermissionGroup> GetFriendDefaults()
    {
        return new List<PermissionGroup>
        {
            new PermissionGroup
            {
                Id = "friend_member",
                Name = "Member",
                Permissions = new List<string>
                {
                    AppPermissions.Chat.Use,
                    AppPermissions.Calendar.View,
                    AppPermissions.Albums.View
                }
            },
            new PermissionGroup
            {
                Id = "friend_admin",
                Name = "Administrator",
                Permissions = new List<string>
                {
                    AppPermissions.Chat.Use,
                    AppPermissions.Calendar.View,
                    AppPermissions.Albums.View,
                    AppPermissions.Chat.CreateChannels,
                    AppPermissions.Chat.ViewArchive,
                    AppPermissions.Chat.EditAllPublicChannels,
                    AppPermissions.Chat.Moderator
                }
            }
        };
    }
}
