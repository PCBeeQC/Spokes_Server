using System;
using System.Collections.Concurrent;

namespace Spokes_Server.Core.Services.Documents;

public class RenderTokenService
{
    private class TokenData
    {
        public DateTime ExpiresAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, TokenData> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _tokenLifetime = TimeSpan.FromMinutes(2);

    public string CreateToken()
    {
        CleanupExpired();

        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = new TokenData { ExpiresAt = DateTime.UtcNow.Add(_tokenLifetime) };
        return token;
    }

    public bool ValidateAndConsume(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        CleanupExpired();

        if (_tokens.TryRemove(token, out var data))
        {
            return DateTime.UtcNow <= data.ExpiresAt;
        }

        return false;
    }

    private void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _tokens)
        {
            if (now > kvp.Value.ExpiresAt)
            {
                _tokens.TryRemove(kvp.Key, out _);
            }
        }
    }
}
