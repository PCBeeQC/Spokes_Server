using System;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class ChatCategoryTests
{
    [Fact]
    public void Equals_And_GetHashCode_Behavior()
    {
        var id = Guid.NewGuid().ToString();
        var category1 = new ChatCategory { Id = id, Name = "General" };
        var category2 = new ChatCategory { Id = id, Name = "Different Name" };
        var category3 = new ChatCategory { Id = Guid.NewGuid().ToString(), Name = "General" };

        // Reference equality
        Assert.True(category1.Equals(category1));

        // Same ID equality
        Assert.True(category1.Equals(category2));
        Assert.Equal(category1.GetHashCode(), category2.GetHashCode());

        // Different ID inequality
        Assert.False(category1.Equals(category3));

        // Null and different type inequality
        Assert.False(category1.Equals(null));
        Assert.False(category1.Equals("some string"));
        Assert.False(category1.Equals(new object()));
    }

    [Fact]
    public void GetHashCode_WhenIdIsNull_UsesBaseHashCode()
    {
        var category = new ChatCategory { Id = null! };

        var hashCode = category.GetHashCode();
        Assert.IsType<int>(hashCode);
    }
}
