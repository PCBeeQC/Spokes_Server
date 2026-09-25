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

        var result = CalendarEffectiveCategory.Resolve(
            evt,
            currentUserId: "emp-current",
            currentUserTeamIds: new HashSet<string>(),
            employeeNames: new Dictionary<string, string>(),
            teamNames: new Dictionary<string, string>());

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

        var result = CalendarEffectiveCategory.Resolve(
            evt,
            currentUserId: "emp-other",
            currentUserTeamIds: new HashSet<string>(),
            employeeNames: new Dictionary<string, string> { ["emp-admin"] = "Admin Boss" },
            teamNames: new Dictionary<string, string>());

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

        var result = CalendarEffectiveCategory.Resolve(
            evt,
            currentUserId: "emp-member",
            currentUserTeamIds: new HashSet<string> { "team-eng" },
            employeeNames: new Dictionary<string, string> { ["emp-creator"] = "Tech Lead" },
            teamNames: new Dictionary<string, string> { ["team-eng"] = "Engineering" });

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

        var result = CalendarEffectiveCategory.Resolve(
            evt,
            currentUserId: "emp-member",
            currentUserTeamIds: new HashSet<string> { "team-unknown" },
            employeeNames: new Dictionary<string, string>(),
            teamNames: new Dictionary<string, string>());

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

        var result = CalendarEffectiveCategory.Resolve(
            evt,
            currentUserId: "emp-bob",
            currentUserTeamIds: new HashSet<string>(),
            employeeNames: new Dictionary<string, string> { ["emp-alice"] = "Alice Smith" },
            teamNames: new Dictionary<string, string>());

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

        var result = CalendarEffectiveCategory.Resolve(
            evt,
            currentUserId: "emp-bob",
            currentUserTeamIds: new HashSet<string>(),
            employeeNames: new Dictionary<string, string>(),
            teamNames: new Dictionary<string, string>());

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

        var result = CalendarEffectiveCategory.Resolve(
            evt,
            currentUserId: "emp-user",
            currentUserTeamIds: new HashSet<string>(),
            employeeNames: new Dictionary<string, string>(),
            teamNames: new Dictionary<string, string>());

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
}
