using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications;

public class NotificationCategoryRegistryTests
{
    [Fact]
    public void GetAllCategories_ReturnsExpectedDefaultCategories()
    {
        var categories = NotificationCategoryRegistry.GetAllCategories();

        Assert.NotEmpty(categories);
        Assert.Contains(categories, c => c.Id == NotificationCategories.Chat);
        Assert.Contains(categories, c => c.Id == NotificationCategories.Call);
        Assert.Contains(categories, c => c.Id == NotificationCategories.Calendar);
    }

    [Fact]
    public void GetAllCategories_AreOrderedBySortOrder()
    {
        var categories = NotificationCategoryRegistry.GetAllCategories();
        var sortOrders = categories.Select(c => c.SortOrder).ToList();

        Assert.Equal(sortOrders.OrderBy(s => s), sortOrders);
    }

    [Theory]
    [InlineData(NotificationCategories.Chat, "Chat Messages", "spokesnotif1.wav")]
    [InlineData(NotificationCategories.Call, "Incoming Calls", "mixkit_marimba_ringtone_1359.wav")]
    [InlineData(NotificationCategories.Calendar, "Calendar Alerts", "mixkit_alert_quick_chime_766.wav")]
    public void GetCategory_ReturnsExpectedDefinition(string categoryId, string expectedName, string expectedSoundFile)
    {
        var category = NotificationCategoryRegistry.GetCategory(categoryId);

        Assert.NotNull(category);
        Assert.Equal(expectedName, category.DisplayName);
        Assert.Equal(expectedSoundFile, category.DefaultSoundFileName);
        Assert.False(string.IsNullOrEmpty(category.Icon));
    }

    [Fact]
    public void GetCategory_IsCaseInsensitive()
    {
        var categoryUpper = NotificationCategoryRegistry.GetCategory("CHAT");
        var categoryLower = NotificationCategoryRegistry.GetCategory("chat");

        Assert.NotNull(categoryUpper);
        Assert.Same(categoryUpper?.Id, categoryLower?.Id);
    }

    [Fact]
    public void GetCategory_ReturnsNullForUnknownCategory()
    {
        var category = NotificationCategoryRegistry.GetCategory("unknown_category_xyz");
        Assert.Null(category);
    }
}
