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
    public void LicenseExpiredException_InheritsFromDomainException_AndSetsErrorCode()
    {
        // Arrange
        var ex = new LicenseExpiredException("Expired");

        // Assert
        Assert.True(typeof(Spokes_Server.Core.Exceptions.DomainException).IsAssignableFrom(typeof(LicenseExpiredException)));
        Assert.True(typeof(Exception).IsAssignableFrom(typeof(LicenseExpiredException)));
        Assert.Equal("LICENSE_EXPIRED", ex.ErrorCode);
    }
}
