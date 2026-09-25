using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Tests.Core.Services.Licensing;

public class LicenseExceptionsTests
{
    [Fact]
    public void LicenseExpiredException_Constructor_SetsMessage()
    {
        // Arrange
        const string expectedMessage = "License has expired";

        // Act
        var ex = new LicenseExpiredException(expectedMessage);

        // Assert
        Assert.Equal(expectedMessage, ex.Message);
    }

    [Fact]
    public void LicenseExpiredException_InheritsFromException()
    {
        // Assert
        Assert.True(typeof(Exception).IsAssignableFrom(typeof(LicenseExpiredException)));
    }
}
