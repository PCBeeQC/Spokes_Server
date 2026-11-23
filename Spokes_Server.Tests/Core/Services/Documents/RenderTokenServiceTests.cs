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
        var token = service.CreateToken("/projects/p1/quotes/q1", "emp-1");

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
            var token = service.CreateToken("/projects/p1/quotes/q1", "emp-1");
            generatedTokens.Add(token);
        }

        // Assert
        Assert.Equal(tokenCount, generatedTokens.Count);
    }

    [Fact]
    public void RevokeToken_RemovesToken_AndSubsequentConsumptionReturnsFalse()
    {
        // Arrange
        var service = new RenderTokenService();
        var token = service.CreateToken("/projects/p1/quotes/q1", "emp-1");
        Assert.True(service.ContainsToken(token));

        // Act
        var revoked = service.RevokeToken(token, out var revokedSessionId);
        var validAfterRevoke = service.TryConsumeToken(token, "/projects/p1/quotes/q1", out _);
        var secondRevoke = service.RevokeToken(token, out _);

        // Assert
        Assert.True(revoked);
        Assert.False(string.IsNullOrWhiteSpace(revokedSessionId));
        Assert.False(validAfterRevoke);
        Assert.False(secondRevoke);
    }

    [Fact]
    public void TryConsumeToken_ValidToken_ReturnsTrueFirstTime_AndFalseSecondTime()
    {
        // Arrange
        var service = new RenderTokenService();
        var token = service.CreateToken(null, "emp-1");

        // Act
        var firstResult = service.TryConsumeToken(token, null, out var sessionId, out var initiator);
        var secondResult = service.TryConsumeToken(token, null, out _);

        // Assert
        Assert.True(firstResult);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.Equal("emp-1", initiator);
        Assert.False(secondResult);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RevokeAndConsume_NullOrWhitespaceToken_ReturnsFalse(string? token)
    {
        // Arrange
        var service = new RenderTokenService();

        // Act & Assert
        Assert.False(service.RevokeToken(token!, out _));
        Assert.False(service.TryConsumeToken(token!, null, out _));
    }

    [Fact]
    public void TryConsumeToken_NonExistentToken_ReturnsFalse()
    {
        // Arrange
        var service = new RenderTokenService();

        // Act & Assert
        Assert.False(service.TryConsumeToken("non-existent-token-12345", null, out _));
    }

    [Fact]
    public void TryConsumeToken_CaseInsensitiveMatching()
    {
        // Arrange
        var service = new RenderTokenService();
        var token = service.CreateToken("/projects/p1/quotes/q1", "emp-1");
        var upperToken = token.ToUpperInvariant();

        // Act
        var result = service.TryConsumeToken(upperToken, "/projects/p1/quotes/q1", out var sessionId);

        // Assert
        Assert.True(result);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
    }

    [Fact]
    public void TryConsumeToken_ExpiredToken_ReturnsFalse()
    {
        // Arrange
        var timeProvider = new TestTimeProvider();
        var service = new RenderTokenService(timeProvider);
        var token = service.CreateToken("/projects/p1/quotes/q1", "emp-1");

        // Advance past 2-minute token lifetime
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        // Act
        var result = service.TryConsumeToken(token, "/projects/p1/quotes/q1", out _);

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
        var expiredToken = service.CreateToken("/projects/p1/quotes/q1", "emp-1");

        // Advance past 2-minute token lifetime
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        Assert.True(service.ContainsToken(expiredToken));

        // Act
        var newToken = service.CreateToken("/projects/p1/quotes/q2", "emp-1");

        // Assert
        Assert.False(service.ContainsToken(expiredToken));
        Assert.True(service.ContainsToken(newToken));
    }

    [Fact]
    public void RevokeToken_AfterSlowRenderPastTokenLifetime_StillReturnsConsumedSessionId()
    {
        // Arrange
        var timeProvider = new TestTimeProvider();
        var service = new RenderTokenService(timeProvider);
        var token = service.CreateToken("/projects/p1/invoices/i1", "emp-1");
        Assert.True(service.TryConsumeToken(token, "/projects/p1/invoices/i1", out var consumedSessionId));

        // Simulate a slow PDF render taking longer than the 2-minute unconsumed token lifetime
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        // Creating a new token triggers CleanupExpired()
        _ = service.CreateToken("/projects/p1/invoices/i2", "emp-1");

        // Act
        var revoked = service.RevokeToken(token, out var revokedSessionId);

        // Assert
        Assert.True(revoked);
        Assert.Equal(consumedSessionId, revokedSessionId);
    }
}
