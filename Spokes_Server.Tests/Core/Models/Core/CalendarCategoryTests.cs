using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Tests.Core.Models.Core;

public class CalendarCategoryTests
{
    [Fact]
    public void CalendarCategory_Initialization_SetsDefaultsCorrectly()
    {
        var category = new CalendarCategory();

        Assert.False(string.IsNullOrWhiteSpace(category.Id));
        Assert.True(Guid.TryParse(category.Id, out _));
        Assert.Equal(string.Empty, category.Name);
        Assert.Equal("Default", category.Color);
    }

    [Fact]
    public void CalendarCategory_PropertyMutations_WorkCorrectly()
    {
        var category = new CalendarCategory
        {
            Id = "cat-custom",
            Name = "Deployment",
            Color = "Primary"
        };

        Assert.Equal("cat-custom", category.Id);
        Assert.Equal("Deployment", category.Name);
        Assert.Equal("Primary", category.Color);
    }

    [Fact]
    public void CalendarCategoryDefaults_GetSystemDefaults_ReturnsExpectedCategories()
    {
        var defaults = CalendarCategoryDefaults.GetSystemDefaults();

        Assert.NotNull(defaults);
        Assert.Equal(5, defaults.Count);

        Assert.Contains(defaults, c => c.Id == "work" && c.Name == "Work" && c.Color == "Info");
        Assert.Contains(defaults, c => c.Id == "meeting" && c.Name == "Meeting" && c.Color == "Primary");
        Assert.Contains(defaults, c => c.Id == "holiday" && c.Name == "Holiday" && c.Color == "Success");
        Assert.Contains(defaults, c => c.Id == "personal" && c.Name == "Personal" && c.Color == "Warning");
        Assert.Contains(defaults, c => c.Id == "important" && c.Name == "Important" && c.Color == "Error");
    }

    [Fact]
    public void CalendarCategoryDefaults_CloneList_NullOrEmpty_ReturnsSystemDefaults()
    {
        var nullResult = CalendarCategoryDefaults.CloneList(null);
        var emptyResult = CalendarCategoryDefaults.CloneList(new List<CalendarCategory>());

        Assert.Equal(5, nullResult.Count);
        Assert.Equal(5, emptyResult.Count);
    }

    [Fact]
    public void CalendarCategoryDefaults_CloneList_WithExistingCategories_ClonesWithNewIds()
    {
        var template = new List<CalendarCategory>
        {
            new() { Id = "work", Name = "Custom Work", Color = "Info" },
            new() { Id = "client", Name = "Client Review", Color = "Secondary" }
        };

        var clones = CalendarCategoryDefaults.CloneList(template);

        Assert.Equal(2, clones.Count);
        Assert.NotEqual("work", clones[0].Id);
        Assert.NotEqual("client", clones[1].Id);
        Assert.True(Guid.TryParse(clones[0].Id, out _));
        Assert.True(Guid.TryParse(clones[1].Id, out _));
        Assert.Equal("Custom Work", clones[0].Name);
        Assert.Equal("Client Review", clones[1].Name);
        Assert.Equal("Info", clones[0].Color);
        Assert.Equal("Secondary", clones[1].Color);
    }

    [Fact]
    public void CalendarCategoryDefaults_CloneList_WithBlankColor_DefaultsToInfo()
    {
        var template = new List<CalendarCategory>
        {
            new() { Id = "c1", Name = "No Color", Color = "" }
        };

        var clones = CalendarCategoryDefaults.CloneList(template);

        Assert.Single(clones);
        Assert.Equal("Info", clones[0].Color);
    }
}
