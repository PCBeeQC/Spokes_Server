using Spokes_Server.Components.Pages.Chat;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Chat;

public class EmojiDataTests
{
    [Fact]
    public void Categories_IsLoadedAndNotEmpty()
    {
        Assert.NotNull(EmojiData.Categories);
        Assert.NotEmpty(EmojiData.Categories);
    }

    [Fact]
    public void Keywords_IsLoadedAndNotEmpty()
    {
        Assert.NotNull(EmojiData.Keywords);
        Assert.NotEmpty(EmojiData.Keywords);
    }

    [Fact]
    public void Keywords_ContainsSearchableTerms()
    {
        // Verify standard emoji entries exist and have their description/aliases indexed in lowercase
        Assert.True(EmojiData.Keywords.ContainsKey("😀"));
        var grinningKeywords = EmojiData.Keywords["😀"];
        Assert.Contains("grinning face", grinningKeywords);
        Assert.Contains("grinning", grinningKeywords);
        Assert.Contains("smile", grinningKeywords);
        Assert.Contains("😀", grinningKeywords);

        // Ensure all characters in keywords (excluding emoji characters themselves) are lowercased
        Assert.Equal(grinningKeywords.ToLowerInvariant(), grinningKeywords);
    }

    [Fact]
    public void Categories_ContainsStandardCategories()
    {
        // Verify standard category names exist and have items
        Assert.Contains("Smileys & Emotion", EmojiData.Categories.Keys);
        Assert.NotEmpty(EmojiData.Categories["Smileys & Emotion"]);
        Assert.Contains("😀", EmojiData.Categories["Smileys & Emotion"]);
    }
}
