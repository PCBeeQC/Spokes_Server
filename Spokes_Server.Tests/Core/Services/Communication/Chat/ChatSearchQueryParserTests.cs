namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

using System;
using Spokes_Server.Core.Services.Communication.Chat;
using Xunit;

public class ChatSearchQueryParserTests
{
    [Fact]
    public void Parse_EmptyOrNullQuery_ReturnsEmptyFilter()
    {
        var filterNull = ChatSearchQueryParser.Parse(null);
        var filterEmpty = ChatSearchQueryParser.Parse("   ");

        Assert.True(filterNull.IsEmpty);
        Assert.True(filterEmpty.IsEmpty);
        Assert.Empty(filterNull.TextTerms);
    }

    [Fact]
    public void Parse_PlainTextTerms_ExtractsKeywords()
    {
        var filter = ChatSearchQueryParser.Parse("hello world \"quarterly report\"");

        Assert.Equal(3, filter.TextTerms.Count);
        Assert.Equal("hello", filter.TextTerms[0]);
        Assert.Equal("world", filter.TextTerms[1]);
        Assert.Equal("quarterly report", filter.TextTerms[2]);
        Assert.Null(filter.HasImages);
        Assert.Null(filter.HasFiles);
    }

    [Theory]
    [InlineData("isPicture:")]
    [InlineData("picture:")]
    [InlineData("image:")]
    [InlineData("photo:")]
    [InlineData("pic:")]
    [InlineData("is:image")]
    [InlineData("is:picture")]
    [InlineData("has:image")]
    [InlineData("has:picture")]
    public void Parse_PictureSynonyms_SetsHasImagesTrue(string tag)
    {
        var filter = ChatSearchQueryParser.Parse($"{tag} budget");

        Assert.True(filter.HasImages);
        Assert.Single(filter.TextTerms);
        Assert.Equal("budget", filter.TextTerms[0]);
    }

    [Theory]
    [InlineData("-isPicture:")]
    [InlineData("!isPicture:")]
    [InlineData("isPicture:false")]
    public void Parse_NegatedPicture_SetsHasImagesFalse(string tag)
    {
        var filter = ChatSearchQueryParser.Parse(tag);

        Assert.False(filter.HasImages);
    }

    [Theory]
    [InlineData("has:file")]
    [InlineData("isFile:")]
    [InlineData("file:")]
    [InlineData("attachment:")]
    [InlineData("doc:")]
    [InlineData("is:file")]
    public void Parse_FileSynonyms_SetsHasFilesTrue(string tag)
    {
        var filter = ChatSearchQueryParser.Parse(tag);

        Assert.True(filter.HasFiles);
    }

    [Theory]
    [InlineData("has:link")]
    [InlineData("link:")]
    [InlineData("url:")]
    [InlineData("has:url")]
    public void Parse_LinkSynonyms_SetsHasLinksTrue(string tag)
    {
        var filter = ChatSearchQueryParser.Parse(tag);

        Assert.True(filter.HasLinks);
    }

    [Fact]
    public void Parse_Senders_SupportsPlainAndQuotedNames()
    {
        var filter = ChatSearchQueryParser.Parse("from:me author:\"John Doe\" sender:Alice");

        Assert.Equal(3, filter.SenderIdentifiers.Count);
        Assert.Equal("me", filter.SenderIdentifiers[0]);
        Assert.Equal("John Doe", filter.SenderIdentifiers[1]);
        Assert.Equal("Alice", filter.SenderIdentifiers[2]);
    }

    [Fact]
    public void Parse_Mentions_SupportsPlainAndQuotedNames()
    {
        var filter = ChatSearchQueryParser.Parse("mentions:me to:\"Bob Smith\"");

        Assert.Equal(2, filter.MentionIdentifiers.Count);
        Assert.Equal("me", filter.MentionIdentifiers[0]);
        Assert.Equal("Bob Smith", filter.MentionIdentifiers[1]);
    }

    [Fact]
    public void Parse_Dates_SupportsIsoAndRelativeTokens()
    {
        var filter = ChatSearchQueryParser.Parse("before:2026-12-31 after:yesterday");

        Assert.NotNull(filter.Before);
        Assert.Equal(2026, filter.Before.Value.Year);
        Assert.Equal(12, filter.Before.Value.Month);
        Assert.Equal(31, filter.Before.Value.Day);

        Assert.NotNull(filter.After);
        Assert.True(filter.After.Value <= DateTime.UtcNow);
    }

    [Fact]
    public void Parse_CombinedComplexQuery_ExtractsAllFiltersAndTermsCleanly()
    {
        var filter = ChatSearchQueryParser.Parse("from:Alice isPicture: has:file budget \"Q3 review\"");

        Assert.Single(filter.SenderIdentifiers);
        Assert.Equal("Alice", filter.SenderIdentifiers[0]);
        Assert.True(filter.HasImages);
        Assert.True(filter.HasFiles);
        Assert.Equal(2, filter.TextTerms.Count);
        Assert.Equal("budget", filter.TextTerms[0]);
        Assert.Equal("Q3 review", filter.TextTerms[1]);
    }

    [Fact]
    public void Parse_ValueTagsWithSpaces_ExtractsValueCleanly()
    {
        var filter = ChatSearchQueryParser.Parse("from: admin before: 2026-10-25 budget");

        Assert.Single(filter.SenderIdentifiers);
        Assert.Equal("admin", filter.SenderIdentifiers[0]);
        Assert.NotNull(filter.Before);
        Assert.Equal(2026, filter.Before.Value.Year);
        Assert.Equal(10, filter.Before.Value.Month);
        Assert.Equal(25, filter.Before.Value.Day);
        Assert.Single(filter.TextTerms);
        Assert.Equal("budget", filter.TextTerms[0]);
    }
}
