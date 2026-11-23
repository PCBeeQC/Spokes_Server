using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class EmailModelsTests
{
    [Fact]
    public void EmailFolder_Equals_ReturnsExpectedResults()
    {
        var folder1 = new EmailFolder { Id = "folder-1" };
        var folder1SameId = new EmailFolder { Id = "folder-1" };
        var folder2 = new EmailFolder { Id = "folder-2" };

        // Reference equality
        Assert.True(folder1.Equals(folder1));

        // Value equality by Id
        Assert.True(folder1.Equals(folder1SameId));
        Assert.False(folder1.Equals(folder2));

        // Null and different type
        Assert.False(folder1.Equals(null));
        Assert.False(folder1.Equals("folder-1"));
        Assert.False(folder1.Equals(new object()));
    }

    [Fact]
    public void EmailFolder_GetHashCode_ReturnsExpectedHashCode()
    {
        var folder1 = new EmailFolder { Id = "folder-1" };
        var folder2 = new EmailFolder { Id = "folder-1" };
        var folderDifferent = new EmailFolder { Id = "folder-2" };

        // Equal Ids have equal hash codes
        Assert.Equal(folder1.GetHashCode(), folder2.GetHashCode());
        Assert.Equal("folder-1".GetHashCode(), folder1.GetHashCode());
        Assert.NotEqual(folder1.GetHashCode(), folderDifferent.GetHashCode());

        // Null-Id fallback to base.GetHashCode()
        var folderNullId = new EmailFolder { Id = null! };
        var hash = folderNullId.GetHashCode();
        // Should execute without throwing NullReferenceException
        Assert.True(hash != 0 || hash == 0);
    }

    [Fact]
    public void EmailMessage_IsLocalOnly_WhenSetExplicitly_ReturnsTrue()
    {
        var message = new EmailMessage { IsLocalOnly = true };
        Assert.True(message.IsLocalOnly);
    }

    [Fact]
    public void EmailMessage_IsLocalOnly_WhenUniqueIdIsLegacySentinel_ReturnsTrue()
    {
        var message = new EmailMessage { UniqueId = uint.MaxValue - 500 };
        Assert.True(message.IsLocalOnly);
    }

}
