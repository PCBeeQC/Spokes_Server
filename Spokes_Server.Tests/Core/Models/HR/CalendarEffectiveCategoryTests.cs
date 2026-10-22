using MudBlazor;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models.HR;

public class CalendarEffectiveCategoryTests
{
    [Fact]
    public void Resolve_WhenOwnEvent_ReturnsOwnFilterKeyAndNoSharingAttribution()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-current",
            Category = "Meeting",
            CategoryColor = "Primary"
        };

        var context = new CalendarViewingContext(
            CurrentUserId: "emp-current",
            CurrentUserTeamIds: new HashSet<string>(),
            EmployeeNames: new Dictionary<string, string>(),
            TeamNames: new Dictionary<string, string>());
        var result = CalendarEffectiveCategory.Resolve(evt, context);

        Assert.Equal("own:meeting", result.FilterKey);
        Assert.Equal("Meeting", result.DisplayName);
        Assert.Equal("Meeting", result.CategoryName);
        Assert.Equal(Color.Primary, result.Color);
        Assert.False(result.IsShared);
        Assert.Null(result.Attribution);
    }

    [Fact]
    public void Resolve_WhenCompanyWideEvent_ReturnsCompanyAttribution()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-admin",
            Category = "Holiday",
            IsCompanyWide = true,
            CategoryColor = "Success"
        };

        var context = new CalendarViewingContext(
            CurrentUserId: "emp-other",
            CurrentUserTeamIds: new HashSet<string>(),
            EmployeeNames: new Dictionary<string, string> { ["emp-admin"] = "Admin Boss" },
            TeamNames: new Dictionary<string, string>());
        var result = CalendarEffectiveCategory.Resolve(evt, context);

        Assert.Equal("company:holiday", result.FilterKey);
        Assert.Equal("Holiday - Company", result.DisplayName);
        Assert.Equal("Holiday", result.CategoryName);
        Assert.Equal(Color.Success, result.Color);
        Assert.True(result.IsShared);
        Assert.Equal("Company", result.Attribution);
    }

    [Fact]
    public void Resolve_WhenTeamEvent_ReturnsTeamAttribution()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-creator",
            Category = "Sprint",
            InvitedTeamIds = ["team-eng"],
            CategoryColor = "Info"
        };

        var context = new CalendarViewingContext(
            CurrentUserId: "emp-member",
            CurrentUserTeamIds: new HashSet<string> { "team-eng" },
            EmployeeNames: new Dictionary<string, string> { ["emp-creator"] = "Tech Lead" },
            TeamNames: new Dictionary<string, string> { ["team-eng"] = "Engineering" });
        var result = CalendarEffectiveCategory.Resolve(evt, context);

        Assert.Equal("team:team-eng:sprint", result.FilterKey);
        Assert.Equal("Sprint - Engineering", result.DisplayName);
        Assert.Equal("Sprint", result.CategoryName);
        Assert.True(result.IsShared);
        Assert.Equal("Engineering", result.Attribution);
    }

    [Fact]
    public void Resolve_WhenTeamEvent_UnknownTeamName_FallsBackToDefaultTeamText()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-creator",
            Category = "Sync",
            InvitedTeamIds = ["team-unknown"]
        };

        var context = new CalendarViewingContext(
            CurrentUserId: "emp-member",
            CurrentUserTeamIds: new HashSet<string> { "team-unknown" },
            EmployeeNames: new Dictionary<string, string>(),
            TeamNames: new Dictionary<string, string>());
        var result = CalendarEffectiveCategory.Resolve(evt, context);

        Assert.Equal("team:team-unknown:sync", result.FilterKey);
        Assert.Equal("Sync - Team", result.DisplayName);
        Assert.Equal("Team", result.Attribution);
    }

    [Fact]
    public void Resolve_WhenDirectShare_ReturnsCreatorAttribution()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-alice",
            Category = "1-on-1",
            Attendees = ["emp-bob"],
            CategoryColor = "Secondary"
        };

        var context = new CalendarViewingContext(
            CurrentUserId: "emp-bob",
            CurrentUserTeamIds: new HashSet<string>(),
            EmployeeNames: new Dictionary<string, string> { ["emp-alice"] = "Alice Smith" },
            TeamNames: new Dictionary<string, string>());
        var result = CalendarEffectiveCategory.Resolve(evt, context);

        Assert.Equal("user:emp-alice:1-on-1", result.FilterKey);
        Assert.Equal("1-on-1 - Shared by Alice Smith", result.DisplayName);
        Assert.Equal("1-on-1", result.CategoryName);
        Assert.True(result.IsShared);
        Assert.Equal("Alice Smith", result.Attribution);
    }

    [Fact]
    public void Resolve_WhenDirectShare_UnknownCreator_FallsBackToUser()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-ghost",
            Category = "Review"
        };

        var context = new CalendarViewingContext(
            CurrentUserId: "emp-bob",
            CurrentUserTeamIds: new HashSet<string>(),
            EmployeeNames: new Dictionary<string, string>(),
            TeamNames: new Dictionary<string, string>());
        var result = CalendarEffectiveCategory.Resolve(evt, context);

        Assert.Equal("user:emp-ghost:review", result.FilterKey);
        Assert.Equal("Review - Shared by User", result.DisplayName);
        Assert.Equal("User", result.Attribution);
    }

    [Fact]
    public void Resolve_WhenCategoryBlank_DefaultsToGeneral()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-user",
            Category = "   "
        };

        var context = new CalendarViewingContext(
            CurrentUserId: "emp-user",
            CurrentUserTeamIds: new HashSet<string>(),
            EmployeeNames: new Dictionary<string, string>(),
            TeamNames: new Dictionary<string, string>());
        var result = CalendarEffectiveCategory.Resolve(evt, context);

        Assert.Equal("own:general", result.FilterKey);
        Assert.Equal("General", result.DisplayName);
        Assert.Equal("General", result.CategoryName);
    }

    [Fact]
    public void ResolveColor_UsesSnapshotColorIfValid()
    {
        var evt = new CalendarEvent
        {
            CategoryColor = "Warning"
        };

        var color = CalendarEffectiveCategory.ResolveColor(evt, null);
        Assert.Equal(Color.Warning, color);
    }

    [Fact]
    public void ResolveColor_MatchesUserCategoryById()
    {
        var evt = new CalendarEvent
        {
            CategoryId = "cat-custom-1",
            CategoryColor = ""
        };

        var userCategories = new Dictionary<string, CalendarCategory>
        {
            ["cat-custom-1"] = new() { Id = "cat-custom-1", Name = "Custom", Color = "Dark" }
        };

        var color = CalendarEffectiveCategory.ResolveColor(evt, userCategories);
        Assert.Equal(Color.Dark, color);
    }

    [Fact]
    public void ResolveColor_MatchesUserCategoryByName()
    {
        var evt = new CalendarEvent
        {
            Category = "Design",
            CategoryId = "",
            CategoryColor = ""
        };

        var userCategories = new Dictionary<string, CalendarCategory>
        {
            ["c-1"] = new() { Id = "c-1", Name = "Design", Color = "Tertiary" }
        };

        var color = CalendarEffectiveCategory.ResolveColor(evt, userCategories);
        Assert.Equal(Color.Tertiary, color);
    }

    [Fact]
    public void ResolveColor_FallsBackToInfoWhenNoMatch()
    {
        var evt = new CalendarEvent
        {
            Category = "Random",
            CategoryId = "not-found",
            CategoryColor = ""
        };

        var color = CalendarEffectiveCategory.ResolveColor(evt, new Dictionary<string, CalendarCategory>());
        Assert.Equal(Color.Info, color);
    }

    [Fact]
    public void Resolve_WhenCategoryRenamedInUserCategories_UsesRenamedNameAndColor()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-current",
            CategoryId = "cat-work",
            Category = "Old Work Name",
            CategoryColor = "Info"
        };

        var userCategories = new Dictionary<string, CalendarCategory>
        {
            ["cat-work"] = new() { Id = "cat-work", Name = "Deep Work Focus", Color = "Secondary" }
        };

        var context = new CalendarViewingContext(
            CurrentUserId: "emp-current",
            CurrentUserTeamIds: new HashSet<string>(),
            EmployeeNames: new Dictionary<string, string>(),
            TeamNames: new Dictionary<string, string>(),
            UserCategories: userCategories);

        var result = CalendarEffectiveCategory.Resolve(evt, context);

        Assert.Equal("Deep Work Focus", result.DisplayName);
        Assert.Equal("Deep Work Focus", result.CategoryName);
        Assert.Equal(Color.Secondary, result.Color);
        Assert.Equal("own:deep work focus", result.FilterKey);
    }
}
