using System.Text.Json;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class LinkPreviewTests
{
    [Fact]
    public void LinkPreview_Properties_WorkCorrectly()
    {
        var preview = new LinkPreview
        {
            Url = "https://example.com",
            Title = "Example Domain",
            Description = "Domain for examples",
            ImageUrl = "https://example.com/logo.png"
        };

        Assert.Equal("https://example.com", preview.Url);
        Assert.Equal("Example Domain", preview.Title);
        Assert.Equal("Domain for examples", preview.Description);
        Assert.Equal("https://example.com/logo.png", preview.ImageUrl);

        var json = JsonSerializer.Serialize(preview);
        var copy = JsonSerializer.Deserialize<LinkPreview>(json);
        Assert.NotNull(copy);
        Assert.Equal(preview.Url, copy.Url);
        Assert.Equal(preview.Title, copy.Title);
    }
}
