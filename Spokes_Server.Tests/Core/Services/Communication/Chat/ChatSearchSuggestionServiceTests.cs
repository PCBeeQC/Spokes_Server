namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Xunit;

public class ChatSearchSuggestionServiceTests
{
    private readonly List<Employee> _employees =
    [
        new() { Id = "emp-1", FirstName = "Alex", LastName = "Tremblay", Email = "alex@spokes.com", IsAdmin = true, IsActive = true },
        new() { Id = "emp-2", FirstName = "Alice", LastName = "Smith", Email = "alice@spokes.com", Position = "Product Manager", IsActive = true },
        new() { Id = "emp-3", FirstName = "Bob", LastName = "Jones", Email = "bob@spokes.com", IsActive = false } // Inactive
    ];

    [Fact]
    public void GetSuggestions_EmptyOrColon_ReturnsCanonicalFilterTags()
    {
        var suggestions = ChatSearchSuggestionService.GetSuggestions("", 0, "emp-1", _employees);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.DisplayText == "isPicture:");
        Assert.Contains(suggestions, s => s.DisplayText == "from:");
        Assert.Contains(suggestions, s => s.DisplayText == "has:file");
    }

    [Theory]
    [InlineData("photo")]
    [InlineData("pic")]
    [InlineData("image")]
    [InlineData("photo:")]
    public void GetSuggestions_TypingPictureSynonyms_SuggestsCanonicalIsPicture(string token)
    {
        var suggestions = ChatSearchSuggestionService.GetSuggestions(token, token.Length, "emp-1", _employees);

        Assert.NotEmpty(suggestions);
        var pictureSuggestion = suggestions.FirstOrDefault(s => s.DisplayText == "isPicture:");
        Assert.NotNull(pictureSuggestion);
        Assert.Equal("isPicture: ", pictureSuggestion.ReplacementText);
    }

    [Theory]
    [InlineData("att")]
    [InlineData("attachment")]
    [InlineData("file")]
    [InlineData("doc")]
    public void GetSuggestions_TypingFileSynonyms_SuggestsCanonicalHasFile(string token)
    {
        var suggestions = ChatSearchSuggestionService.GetSuggestions(token, token.Length, "emp-1", _employees);

        Assert.NotEmpty(suggestions);
        var fileSuggestion = suggestions.FirstOrDefault(s => s.DisplayText == "has:file");
        Assert.NotNull(fileSuggestion);
        Assert.Equal("has:file ", fileSuggestion.ReplacementText);
    }

    [Fact]
    public void GetSuggestions_TypingFrom_SuggestsMeAndActiveEmployees()
    {
        var suggestions = ChatSearchSuggestionService.GetSuggestions("from:", 5, "emp-1", _employees);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.DisplayText == "from:me");
        Assert.Contains(suggestions, s => s.DisplayText == "from:admin");
        Assert.Contains(suggestions, s => s.DisplayText == "Alex Tremblay");
        Assert.Contains(suggestions, s => s.DisplayText == "Alice Smith");
        // Bob is inactive, should not appear
        Assert.DoesNotContain(suggestions, s => s.DisplayText == "Bob Jones");
    }

    [Fact]
    public void GetSuggestions_TypingFromWithSpace_SuggestsAdminAndMembers()
    {
        var suggestions = ChatSearchSuggestionService.GetSuggestions("from: ", 6, "emp-1", _employees);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.DisplayText == "from:admin");
        Assert.Contains(suggestions, s => s.DisplayText == "Alice Smith");
    }

    [Fact]
    public void GetSuggestions_TypingBefore_SuggestsSyntaxExample()
    {
        var suggestions = ChatSearchSuggestionService.GetSuggestions("before:", 7, "emp-1", _employees);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.Description.Contains("YYYY-MM-DD"));
    }

    [Fact]
    public void GetSuggestions_TypingFromWithFilter_FiltersMatchingMembers()
    {
        var suggestions = ChatSearchSuggestionService.GetSuggestions("from:ali", 8, "emp-1", _employees);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.DisplayText == "Alice Smith");
        Assert.DoesNotContain(suggestions, s => s.DisplayText == "Alex Tremblay");
    }

    [Fact]
    public void GetSuggestions_TypingBeforeOrAfter_SuggestsDatePresets()
    {
        var suggestions = ChatSearchSuggestionService.GetSuggestions("before:", 7, "emp-1", _employees);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.DisplayText == "before:today");
        Assert.Contains(suggestions, s => s.DisplayText == "before:yesterday");
        Assert.Contains(suggestions, s => s.DisplayText == "before:thisweek");
    }

    [Fact]
    public void GetSuggestions_TypingHas_SuggestsSubTags()
    {
        var suggestions = ChatSearchSuggestionService.GetSuggestions("has:", 4, "emp-1", _employees);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.DisplayText == "has:file");
        Assert.Contains(suggestions, s => s.DisplayText == "has:link");
    }
}
