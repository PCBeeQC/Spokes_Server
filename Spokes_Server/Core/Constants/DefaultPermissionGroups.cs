using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Constants;

public static class DefaultPermissionGroups
{
    public static List<PermissionGroup> GetRoleDefaults() =>
    [
        new PermissionGroup
        {
            Id = "role_employee",
            Name = "Employee (Base)",
            Permissions =
            [
                AppPermissions.Chat.Use,
                AppPermissions.Email.Use,
                AppPermissions.AddressBook.View,
                AppPermissions.Timesheets.AccessTimesheet,
                AppPermissions.Finance.AccessExpenses,
                AppPermissions.Calendar.View,
                AppPermissions.Albums.View
            ]
        },
        new PermissionGroup
        {
            Id = "role_pm",
            Name = "Project Manager",
            Permissions =
            [
                AppPermissions.Projects.View,
                AppPermissions.Projects.Create,
                AppPermissions.Projects.Edit,
                AppPermissions.BusinessDocuments.View,
                AppPermissions.AddressBook.View,
                AppPermissions.Chat.CreateChannels
            ]
        },
        new PermissionGroup
        {
            Id = "role_purchasing",
            Name = "Purchasing / Buyer",
            Permissions =
            [
                AppPermissions.Purchases.ViewPOs,
                AppPermissions.Purchases.ManagePOs,
                AppPermissions.AddressBook.View
            ]
        },
        new PermissionGroup
        {
            Id = "role_accounting",
            Name = "Accounting / Finance",
            Permissions =
            [
                AppPermissions.Finance.AccessAccounting,
                AppPermissions.Purchases.ViewBills,
                AppPermissions.Purchases.ManageBills,
                AppPermissions.Purchases.ApproveBills,
                AppPermissions.AddressBook.View,
                AppPermissions.AddressBook.Manage
            ]
        },
        new PermissionGroup
        {
            Id = "role_team_leader",
            Name = "Team Leader",
            Permissions =
            [
                AppPermissions.Timesheets.ViewReport,
                AppPermissions.MyWeek.Edit,
                AppPermissions.Finance.TeamCapacity
            ]
        },
        new PermissionGroup
        {
            Id = "role_executive",
            Name = "Business Executive",
            Permissions =
            [
                AppPermissions.Finance.AccessAccounting,
                AppPermissions.Finance.TeamCapacity,
                AppPermissions.Timesheets.ViewReport,
                AppPermissions.Projects.View,
                AppPermissions.Purchases.ViewPOs
            ]
        },
        new PermissionGroup
        {
            Id = "role_admin",
            Name = "System Administrator",
            Permissions =
            [
                AppPermissions.Admin.ManageUsers,
                AppPermissions.Admin.ManageSettings,
                AppPermissions.Chat.Moderator
            ]
        }
    ];

    public static List<PermissionGroup> GetAllBusinessDefaults() =>
        [.. GetRoleDefaults(), .. GetBusinessDefaults()];

    public static List<PermissionGroup> GetBusinessDefaults() =>
    [
        new PermissionGroup
        {
            Id = "business_employee",
            Name = "Employee",
            Permissions =
            [
                AppPermissions.Chat.Use,
                AppPermissions.Email.Use,
                AppPermissions.AddressBook.View,
                AppPermissions.Timesheets.AccessTimesheet,
                AppPermissions.Finance.AccessExpenses,
                AppPermissions.Calendar.View,
                AppPermissions.Albums.View
            ]
        },
        new PermissionGroup
        {
            Id = "business_manager",
            Name = "Manager",
            Permissions =
            [
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
            ]
        },
        new PermissionGroup
        {
            Id = "business_director",
            Name = "Director",
            Permissions =
            [
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
            ]
        }
    ];

    public static List<PermissionGroup> GetFriendDefaults() =>
    [
        new PermissionGroup
        {
            Id = "friend_member",
            Name = "Member",
            Permissions =
            [
                AppPermissions.Chat.Use,
                AppPermissions.Calendar.View,
                AppPermissions.Albums.View
            ]
        },
        new PermissionGroup
        {
            Id = "friend_admin",
            Name = "Administrator",
            Permissions =
            [
                AppPermissions.Chat.Use,
                AppPermissions.Calendar.View,
                AppPermissions.Albums.View,
                AppPermissions.Chat.CreateChannels,
                AppPermissions.Chat.ViewArchive,
                AppPermissions.Chat.EditAllPublicChannels,
                AppPermissions.Chat.Moderator
            ]
        }
    ];
}
