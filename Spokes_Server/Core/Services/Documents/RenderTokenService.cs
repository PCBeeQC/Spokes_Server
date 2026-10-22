using System.Collections.Concurrent;

namespace Spokes_Server.Core.Services.Documents;

public class RenderTokenService
{
    private sealed class TokenData
    {
        public required string SessionId { get; init; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, TokenData> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _tokenLifetime = TimeSpan.FromMinutes(2);
    private readonly TimeProvider _timeProvider;

    public RenderTokenService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal int TokenCount => _tokens.Count;
    internal bool ContainsToken(string token) => _tokens.ContainsKey(token);

    public string CreateToken()
    {
        CleanupExpired();

        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = new TokenData
        {
            SessionId = Guid.NewGuid().ToString(),
            ExpiresAt = _timeProvider.GetUtcNow().Add(_tokenLifetime)
        };
        return token;
    }

    /// <summary>
    /// Validates an active render token without consuming it so that all HTTP and WebSocket
    /// requests made by the headless browser during a single PDF render scope are authenticated.
    /// The token is deterministically revoked by <see cref="RevokeToken(string, out string?)"/>
    /// when the render completes, or automatically after <see cref="_tokenLifetime"/>.
    /// </summary>
    public bool ValidateToken(string token, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;

        CleanupExpired();

        if (_tokens.TryGetValue(token, out var data) && _timeProvider.GetUtcNow() <= data.ExpiresAt)
        {
            sessionId = data.SessionId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Explicitly revokes a render token upon completion of a PDF generation scope.
    /// </summary>
    public bool RevokeToken(string token, out string? sessionId)
    {
        sessionId = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        if (_tokens.TryRemove(token, out var data))
        {
            sessionId = data.SessionId;
            return true;
        }

        return false;
    }

    public bool RevokeToken(string token) => RevokeToken(token, out _);

    public bool ValidateAndConsume(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        CleanupExpired();

        return _tokens.TryRemove(token, out var data) && _timeProvider.GetUtcNow() <= data.ExpiresAt;
    }

    private void CleanupExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var kvp in _tokens)
        {
            if (now > kvp.Value.ExpiresAt)
            {
                _tokens.TryRemove(kvp.Key, out _);
            }
        }
    }
}
