using System;
using System.Collections.Generic;
using Spokes_Server.Core.Services.Documents;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Documents;

public class RenderTokenServiceTests
{
    private class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
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
    public void ValidateToken_ValidToken_AllowsMultipleRequestsInSameScope_AndReturnsSameSessionId()
    {
        // Arrange
        var service = new RenderTokenService();
        var token = service.CreateToken();

        // Act
        var firstResult = service.ValidateToken(token, out var sessionId1);
        var secondResult = service.ValidateToken(token, out var sessionId2);

        // Assert
        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.False(string.IsNullOrWhiteSpace(sessionId1));
        Assert.Equal(sessionId1, sessionId2);
    }

    [Fact]
    public void RevokeToken_RemovesToken_AndSubsequentValidationReturnsFalse()
    {
        // Arrange
        var service = new RenderTokenService();
        var token = service.CreateToken();
        Assert.True(service.ValidateToken(token, out var expectedSessionId));

        // Act
        var revoked = service.RevokeToken(token, out var revokedSessionId);
        var validAfterRevoke = service.ValidateToken(token, out _);
        var secondRevoke = service.RevokeToken(token);

        // Assert
        Assert.True(revoked);
        Assert.Equal(expectedSessionId, revokedSessionId);
        Assert.False(validAfterRevoke);
        Assert.False(secondRevoke);
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
    public void ValidateToken_And_ValidateAndConsume_NullOrWhitespaceToken_ReturnsFalse(string? token)
    {
        // Arrange
        var service = new RenderTokenService();

        // Act & Assert
        Assert.False(service.ValidateToken(token!, out _));
        Assert.False(service.RevokeToken(token!, out _));
        Assert.False(service.ValidateAndConsume(token!));
    }

    [Fact]
    public void ValidateToken_NonExistentToken_ReturnsFalse()
    {
        // Arrange
        var service = new RenderTokenService();

        // Act & Assert
        Assert.False(service.ValidateToken("non-existent-token-12345", out _));
        Assert.False(service.ValidateAndConsume("non-existent-token-12345"));
    }

    [Fact]
    public void ValidateToken_CaseInsensitiveMatching()
    {
        // Arrange
        var service = new RenderTokenService();
        var token = service.CreateToken();
        var upperToken = token.ToUpperInvariant();

        // Act
        var result = service.ValidateToken(upperToken, out var sessionId);

        // Assert
        Assert.True(result);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
    }

    [Fact]
    public void ValidateToken_ExpiredToken_ReturnsFalse()
    {
        // Arrange
        var timeProvider = new TestTimeProvider();
        var service = new RenderTokenService(timeProvider);
        var token = service.CreateToken();

        // Advance past 2-minute token lifetime
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        // Act
        var result = service.ValidateToken(token, out _);

        // Assert
        Assert.False(result);
        Assert.False(service.ContainsToken(token));
    }

    [Fact]
    public void CleanupExpired_RemovesExpiredTokensDuringCreateToken()
    {
        // Arrange
        var timeProvider = new TestTimeProvider();
        var service = new RenderTokenService(timeProvider);
        var expiredToken = service.CreateToken();

        // Advance past 2-minute token lifetime
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        Assert.True(service.ContainsToken(expiredToken));

        // Act
        var newToken = service.CreateToken();

        // Assert
        Assert.False(service.ContainsToken(expiredToken));
        Assert.True(service.ContainsToken(newToken));
    }
}
