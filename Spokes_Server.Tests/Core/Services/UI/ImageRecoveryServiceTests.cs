using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Tests.Core.Services.UI;

public class ImageRecoveryServiceTests
{
    [Fact]
    public void NotifyTokensWiped_InvokesOnTokensWipedEvent()
    {
        // Arrange
        var service = new ImageRecoveryService();
        var invoked = false;
        service.OnTokensWiped += () => invoked = true;

        // Act
        service.NotifyTokensWiped();

        // Assert
        Assert.True(invoked);
    }

    [Fact]
    public void NotifyTokensWiped_WhenNoSubscribers_DoesNotThrow()
    {
        // Arrange
        var service = new ImageRecoveryService();

        // Act
        var exception = Record.Exception(() => service.NotifyTokensWiped());

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void NotifyTokensWiped_MultipleSubscribers_InvokesAll()
    {
        // Arrange
        var service = new ImageRecoveryService();
        var count1 = 0;
        var count2 = 0;
        var count3 = 0;

        service.OnTokensWiped += () => count1++;
        service.OnTokensWiped += () => count2++;
        service.OnTokensWiped += () => count3++;

        // Act
        service.NotifyTokensWiped();

        // Assert
        Assert.Equal(1, count1);
        Assert.Equal(1, count2);
        Assert.Equal(1, count3);
    }
}
