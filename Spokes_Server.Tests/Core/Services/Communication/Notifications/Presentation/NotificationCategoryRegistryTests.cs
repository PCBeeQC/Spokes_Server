using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications.Presentation;

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
        Assert.Contains(categories, c => c.Id == NotificationCategories.Email);
    }

    [Fact]
    public void GetAllCategories_FiltersByEdition()
    {
        var businessCategories = NotificationCategoryRegistry.GetAllCategories(isBusinessEdition: true);
        var familyCategories = NotificationCategoryRegistry.GetAllCategories(isBusinessEdition: false);

        Assert.Contains(businessCategories, c => c.Id == NotificationCategories.Email);
        Assert.DoesNotContain(familyCategories, c => c.Id == NotificationCategories.Email);

        Assert.Contains(familyCategories, c => c.Id == NotificationCategories.Chat);
        Assert.Contains(familyCategories, c => c.Id == NotificationCategories.Call);
        Assert.Contains(familyCategories, c => c.Id == NotificationCategories.Calendar);
    }

    [Fact]
    public void GetAllCategories_AreOrderedBySortOrder()
    {
        var categories = NotificationCategoryRegistry.GetAllCategories();
        var sortOrders = categories.Select(c => c.SortOrder).ToList();

        Assert.Equal(sortOrders.OrderBy(s => s), sortOrders);
    }

    [Theory]
    [InlineData(NotificationCategories.Chat, "Chat Messages", "spokesnotif1.wav", false)]
    [InlineData(NotificationCategories.Call, "Incoming Calls", "mixkit_marimba_ringtone_1359.wav", false)]
    [InlineData(NotificationCategories.Calendar, "Calendar Alerts", "mixkit_alert_quick_chime_766.wav", false)]
    [InlineData(NotificationCategories.Email, "Incoming Emails", "spokesnotif1.wav", true)]
    public void GetCategory_ReturnsExpectedDefinition(string categoryId, string expectedName, string expectedSoundFile, bool isBusinessOnly)
    {
        var category = NotificationCategoryRegistry.GetCategory(categoryId);

        Assert.NotNull(category);
        Assert.Equal(expectedName, category.DisplayName);
        Assert.Equal(expectedSoundFile, category.DefaultSoundFileName);
        Assert.Equal(isBusinessOnly, category.IsBusinessOnly);
        Assert.False(string.IsNullOrEmpty(category.Icon));
    }

    [Fact]
    public void GetCategory_IsCaseInsensitive()
    {
        var categoryUpper = NotificationCategoryRegistry.GetCategory("CHAT");
        var categoryLower = NotificationCategoryRegistry.GetCategory("chat");

        Assert.NotNull(categoryUpper);
        Assert.Same(categoryUpper?.Id, categoryLower?.Id);

        var emailUpper = NotificationCategoryRegistry.GetCategory("EMAIL");
        var emailLower = NotificationCategoryRegistry.GetCategory("email");
        Assert.NotNull(emailUpper);
        Assert.Same(emailUpper?.Id, emailLower?.Id);
    }

    [Fact]
    public void GetCategory_ReturnsNullForUnknownCategory()
    {
        var category = NotificationCategoryRegistry.GetCategory("unknown_category_xyz");
        Assert.Null(category);
    }
}
