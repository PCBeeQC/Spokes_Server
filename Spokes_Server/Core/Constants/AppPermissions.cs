namespace Spokes_Server.Core.Constants;

public static class AppPermissions
{
    public static class Admin
    {
        public const string RoleName = "Admin";
        public const string PolicyName = "AdminAccess";
        public const string ManageUsers = "Admin.ManageUsers";
        public const string ManageSettings = "Admin.ManageSettings";
    }

    public static class Projects
    {
        public const string View = "Projects.View";
        public const string Create = "Projects.Create";
        public const string Edit = "Projects.Edit";
        public const string Delete = "Projects.Delete";
    }

    public static class Chat
    {
        public const string Use = "Chat.Use";
        public const string CreateChannels = "Chat.CreateChannels";
        public const string ViewArchive = "Chat.ViewArchive";
        public const string EditAllPublicChannels = "Chat.EditAllPublicChannels";
        public const string Moderator = "Chat.Moderator";
    }

    public static class Email
    {
        public const string Use = "Email.Use";
    }

    public static class AddressBook
    {
        public const string View = "AddressBook.View";
        public const string Manage = "AddressBook.Manage";
    }

    public static class Finance
    {
        public const string AccessAccounting = "Finance.AccessAccounting";
        public const string AccessExpenses = "Finance.AccessExpenses";
        public const string TeamCapacity = "Finance.TeamCapacity";
    }

    public static class Purchases
    {
        public const string ViewPOs = "Purchases.ViewPOs";
        public const string ManagePOs = "Purchases.ManagePOs";
        public const string ViewBills = "Purchases.ViewBills";
        public const string ManageBills = "Purchases.ManageBills";
        public const string ApproveBills = "Purchases.ApproveBills";
    }

    public static class Timesheets
    {
        public const string AccessTimesheet = "Timesheets.AccessTimesheet";
        public const string ViewReport = "Timesheets.ViewReport";
    }

    public static class BusinessDocuments
    {
        public const string View = "BusinessDocuments.View";
    }

    public static class Albums
    {
        public const string View = "Albums.View";
    }

    /// <summary>
    /// Helper to get all permissions for the UI
    /// </summary>
    public static Dictionary<string, List<string>> GetAll()
    {
        return new Dictionary<string, List<string>>
        {
            { "Admin", new List<string> { Admin.ManageUsers, Admin.ManageSettings } },
            { "Projects", new List<string> { Projects.View, Projects.Create, Projects.Edit, Projects.Delete } },
            { "Chat", new List<string> { Chat.Use, Chat.CreateChannels, Chat.ViewArchive, Chat.EditAllPublicChannels, Chat.Moderator } },
            { "Email", new List<string> { Email.Use } },
            { "Address Book", new List<string> { AddressBook.View, AddressBook.Manage } },
            { "Finance", new List<string> { Finance.AccessAccounting, Finance.AccessExpenses, Finance.TeamCapacity } },
            { "Purchases", new List<string> { Purchases.ViewPOs, Purchases.ManagePOs, Purchases.ViewBills, Purchases.ManageBills, Purchases.ApproveBills } },
            { "Timesheets", new List<string> { Timesheets.AccessTimesheet, Timesheets.ViewReport } },
            { "Business Documents", new List<string> { BusinessDocuments.View } },
            { "Albums", new List<string> { Albums.View } },
            { "My Week", new List<string> { MyWeek.Edit } },
            { "Calendar", new List<string> { Calendar.View } }
        };
    }

    /// <summary>
    /// Gets a user-friendly description for the specified permission.
    /// </summary>
    public static string GetDescription(string permission)
    {
        return permission switch
        {
            Admin.ManageUsers => "Full admin access to create, edit, and modify users.",
            Admin.ManageSettings => "Access to system-wide settings and configurations.",
            Projects.View => "Allows viewing projects.",
            Projects.Create => "Allows creating new projects.",
            Projects.Edit => "Allows editing existing project details.",
            Projects.Delete => "Allows deleting projects.",
            Chat.Use => "Allows using the team chat.",
            Chat.CreateChannels => "Allows creating new chat channels.",
            Chat.ViewArchive => "Allows viewing archived chat channels.",
            Chat.EditAllPublicChannels => "Allows editing all public channels, even if not the creator.",
            Chat.Moderator => "Allows moderating chat channels (e.g. deleting reported messages).",
            Email.Use => "Allows sending and receiving emails.",
            AddressBook.View => "Allows viewing the address book.",
            AddressBook.Manage => "Allows adding, editing, and deleting contacts in the address book.",
            Finance.AccessAccounting => "Provides access to the accounting dashboard.",
            Finance.AccessExpenses => "Allows accessing expense reports.",
            Finance.TeamCapacity => "Allows viewing team capacity reports.",
            Purchases.ViewPOs => "Allows viewing purchase orders.",
            Purchases.ManagePOs => "Allows creating, editing, and deleting purchase orders.",
            Purchases.ViewBills => "Allows viewing bills.",
            Purchases.ManageBills => "Allows creating, editing, and deleting bills.",
            Purchases.ApproveBills => "Allows reviewing and approving bills for payment.",
            Timesheets.AccessTimesheet => "Allows accessing personal timesheets.",
            Timesheets.ViewReport => "Allows viewing timesheet reports for the team.",
            BusinessDocuments.View => "Allows accessing and managing business documents.",
            Albums.View => "Allows viewing and interacting with albums.",
            MyWeek.Edit => "Allows editing the My Week view.",
            Calendar.View => "Allows accessing the calendar.",
            _ => "Unknown permission."
        };
    }

    public static class MyWeek
    {
        public const string Edit = "MyWeek.Edit";
    }

    public static class Calendar
    {
        public const string View = "Calendar.View";
    }
}
