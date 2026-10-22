using System;
using Spokes_Server.Components.Layout;
using Xunit;

namespace Spokes_Server.Tests.Components.Layout;

public class MainLayoutRouteTests
{
    [Theory]
    // Friend / Family edition: root URL is the chat home view
    [InlineData("http://localhost:5000/", true, true)]
    [InlineData("http://localhost:5000", true, true)]
    [InlineData("https://spokes.example.com/", true, true)]
    [InlineData("https://spokes.example.com/?installed=true", true, true)]
    [InlineData("/", true, true)]
    [InlineData("/?ref=shortcut", true, true)]
    
    // Business edition: root URL is the business dashboard, NOT chat
    [InlineData("http://localhost:5000/", false, false)]
    [InlineData("http://localhost:5000", false, false)]
    [InlineData("https://spokes.example.com/", false, false)]
    [InlineData("https://spokes.example.com/?installed=true", false, false)]
    [InlineData("/", false, false)]
    
    // Chat routes are always chat layout regardless of edition
    [InlineData("http://localhost:5000/chat", true, true)]
    [InlineData("http://localhost:5000/chat", false, true)]
    [InlineData("http://localhost:5000/chat/", true, true)]
    [InlineData("http://localhost:5000/chat/", false, true)]
    [InlineData("http://localhost:5000/chat/channel-general", true, true)]
    [InlineData("http://localhost:5000/chat/channel-general", false, true)]
    [InlineData("http://localhost:5000/chat/channel-general?t=123", true, true)]
    [InlineData("/chat", true, true)]
    [InlineData("/chat", false, true)]
    [InlineData("/chat/channel-1", true, true)]
    [InlineData("/chat/channel-1", false, true)]
    
    // Non-chat routes are never chat layout
    [InlineData("http://localhost:5000/projects", true, false)]
    [InlineData("http://localhost:5000/projects", false, false)]
    [InlineData("http://localhost:5000/planning/calendar", true, false)]
    [InlineData("http://localhost:5000/planning/calendar", false, false)]
    [InlineData("http://localhost:5000/admin/settings", true, false)]
    [InlineData("http://localhost:5000/albums", true, false)]
    [InlineData("/projects", true, false)]
    [InlineData("/albums", true, false)]
    public void IsChatPageUrl_ClassifiesRoutesCorrectly(string url, bool isFriendEdition, bool expected)
    {
        var result = MainLayout.IsChatPageUrl(url, isFriendEdition);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("", true)]
    [InlineData("", false)]
    public void IsChatPageUrl_ReturnsFalse_WhenUrlIsNullOrWhiteSpace(string? url, bool isFriendEdition)
    {
        var result = MainLayout.IsChatPageUrl(url, isFriendEdition);
        Assert.False(result);
    }

    [Theory]
    [InlineData("http://localhost:5000/chat/ch-general", true)]
    [InlineData("http://localhost:5000/chat/ch-123?filter=all", true)]
    [InlineData("https://spokes.local/chat/c-456?client=mobile", true)]
    [InlineData("http://localhost:5000/chat/ch-123#bottom", true)]
    [InlineData("http://localhost:5000/chat", false)]
    [InlineData("http://localhost:5000/chat/", false)]
    [InlineData("https://spokes.local/chat?client=mobile", false)]
    [InlineData("http://localhost:5000/", false)]
    [InlineData("http://localhost:5000/?ref=shortcut", false)]
    [InlineData("http://localhost:5000/projects/123", false)]
    public void IsChatChannelUrl_IdentifiesChannelRoutes(string url, bool expected)
    {
        var result = MainLayout.IsChatChannelUrl(url);
        Assert.Equal(expected, result);
    }
}
