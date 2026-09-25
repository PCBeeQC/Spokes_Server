using System.Collections;
using System.Reflection;
using Spokes_Server.Core.Services.Documents;

namespace Spokes_Server.Tests.Core.Services.Documents;

public class RenderTokenServiceTests
{
    private static IDictionary GetTokensDictionary(RenderTokenService service)
    {
        var field = typeof(RenderTokenService).GetField("_tokens", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (IDictionary)field.GetValue(service)!;
    }

    private static void InjectToken(RenderTokenService service, string token, DateTime expiresAt)
    {
        var tokens = GetTokensDictionary(service);
        var tokenDataType = typeof(RenderTokenService).GetNestedType("TokenData", BindingFlags.NonPublic);
        Assert.NotNull(tokenDataType);
        var tokenData = Activator.CreateInstance(tokenDataType)!;
        var expiresAtProp = tokenDataType.GetProperty("ExpiresAt");
        Assert.NotNull(expiresAtProp);
        expiresAtProp.SetValue(tokenData, expiresAt);
        tokens[token] = tokenData;
    }

    [Fact]
    public void CreateToken_ReturnsNonEmpty32HexCharacterString()
    {
        // Arrange
        var service = new RenderTokenService();

        // Act
        var token = service.CreateToken();

        // Assert
        Assert.NotNull(token);
        Assert.Equal(32, token.Length);
        Assert.Matches("^[a-f0-9]{32}$", token);
    }

    [Fact]
    public void CreateToken_GeneratesUniqueTokens()
    {
        // Arrange
        var service = new RenderTokenService();
        const int tokenCount = 100;
        var generatedTokens = new HashSet<string>();

        // Act
        for (var i = 0; i < tokenCount; i++)
        {
            var token = service.CreateToken();
            generatedTokens.Add(token);
        }

        // Assert
        Assert.Equal(tokenCount, generatedTokens.Count);
    }

    [Fact]
    public void ValidateAndConsume_ValidToken_ReturnsTrueFirstTime_AndFalseSecondTime()
    {
        // Arrange
        var service = new RenderTokenService();
        var token = service.CreateToken();

        // Act
        var firstResult = service.ValidateAndConsume(token);
        var secondResult = service.ValidateAndConsume(token);

        // Assert
        Assert.True(firstResult);
        Assert.False(secondResult);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateAndConsume_NullOrWhitespaceToken_ReturnsFalse(string? token)
    {
        // Arrange
        var service = new RenderTokenService();

        // Act
        var result = service.ValidateAndConsume(token!);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ValidateAndConsume_NonExistentToken_ReturnsFalse()
    {
        // Arrange
        var service = new RenderTokenService();

        // Act
        var result = service.ValidateAndConsume("non-existent-token-12345");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ValidateAndConsume_CaseInsensitiveMatching()
    {
        // Arrange
        var service = new RenderTokenService();
        var token = service.CreateToken();
        var upperToken = token.ToUpperInvariant();

        // Act
        var result = service.ValidateAndConsume(upperToken);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ValidateAndConsume_ExpiredToken_ReturnsFalse()
    {
        // Arrange
        var service = new RenderTokenService();
        var token = service.CreateToken();
        var tokens = GetTokensDictionary(service);
        var tokenData = tokens[token]!;
        var expiresAtProp = tokenData.GetType().GetProperty("ExpiresAt")!;
        expiresAtProp.SetValue(tokenData, DateTime.UtcNow.AddMinutes(-5));

        // Act
        var result = service.ValidateAndConsume(token);

        // Assert
        Assert.False(result);
        Assert.False(tokens.Contains(token));
    }

    [Fact]
    public void CleanupExpired_RemovesExpiredTokensDuringCreateToken()
    {
        // Arrange
        var service = new RenderTokenService();
        const string expiredToken = "expired-token-123";
        InjectToken(service, expiredToken, DateTime.UtcNow.AddMinutes(-5));

        var tokens = GetTokensDictionary(service);
        Assert.True(tokens.Contains(expiredToken));

        // Act
        var newToken = service.CreateToken();

        // Assert
        Assert.False(tokens.Contains(expiredToken));
        Assert.True(tokens.Contains(newToken));
    }
}
