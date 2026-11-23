using System.Collections.Concurrent;

namespace Spokes_Server.Core.Services.Documents;

public class RenderTokenService
{
    private sealed class TokenData
    {
        public required string SessionId { get; init; }
        public string? ExpectedPath { get; init; }
        public string? InitiatorEmployeeId { get; init; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, TokenData> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TokenData> _consumedSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _tokenLifetime = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _consumedSessionRetention = TimeSpan.FromHours(1);
    private readonly TimeProvider _timeProvider;

    public RenderTokenService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal int TokenCount => _tokens.Count;
    internal bool ContainsToken(string token) => _tokens.ContainsKey(token);

    public string CreateToken(string? expectedPath, string? initiatorEmployeeId)
    {
        CleanupExpired();

        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = new TokenData
        {
            SessionId = Guid.NewGuid().ToString(),
            ExpectedPath = string.IsNullOrWhiteSpace(expectedPath) ? null : NormalizePath(expectedPath),
            InitiatorEmployeeId = string.IsNullOrWhiteSpace(initiatorEmployeeId) ? null : initiatorEmployeeId.Trim(),
            ExpiresAt = _timeProvider.GetUtcNow().Add(_tokenLifetime)
        };
        return token;
    }

    /// <summary>
    /// Atomically validates and consumes a single-use render token bound to <paramref name="requestPath"/>.
    /// Once consumed, the initial loopback navigation receives a short-lived isolated session cookie,
    /// and the token itself can never be replayed.
    /// </summary>
    public bool TryConsumeToken(string token, string? requestPath, out string sessionId)
        => TryConsumeToken(token, requestPath, out sessionId, out _);

    /// <summary>
    /// Atomically validates and consumes a single-use render token bound to <paramref name="requestPath"/>
    /// and an initiating employee ID.
    /// </summary>
    public bool TryConsumeToken(string token, string? requestPath, out string sessionId, out string? initiatorEmployeeId)
    {
        sessionId = string.Empty;
        initiatorEmployeeId = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        CleanupExpired();

        if (!_tokens.TryGetValue(token, out var peek))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow() > peek.ExpiresAt)
        {
            _tokens.TryRemove(token, out _);
            return false;
        }

        if (!string.IsNullOrEmpty(peek.ExpectedPath) && !PathsMatch(requestPath, peek.ExpectedPath))
        {
            return false;
        }

        if (_tokens.TryRemove(token, out var data) && _timeProvider.GetUtcNow() <= data.ExpiresAt)
        {
            if (!string.IsNullOrEmpty(data.ExpectedPath) && !PathsMatch(requestPath, data.ExpectedPath))
            {
                return false;
            }

            // Retain the consumed session mapping beyond _tokenLifetime so DocumentPdfService.finally
            // can still revoke its temporary system-renderer DeviceSession even on slow PDF renders.
            data.ExpiresAt = _timeProvider.GetUtcNow().Add(_consumedSessionRetention);
            _consumedSessions[token] = data;
            sessionId = data.SessionId;
            initiatorEmployeeId = data.InitiatorEmployeeId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Explicitly revokes a render token (or retrieves the session ID of an already-consumed token)
    /// upon completion of a PDF generation scope so its temporary DeviceSession is deleted.
    /// </summary>
    public bool RevokeToken(string token, out string? sessionId)
    {
        sessionId = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        if (_tokens.TryRemove(token, out var data))
        {
            _consumedSessions.TryRemove(token, out _);
            sessionId = data.SessionId;
            return true;
        }

        if (_consumedSessions.TryRemove(token, out var consumed))
        {
            sessionId = consumed.SessionId;
            return true;
        }

        return false;
    }

    private static bool PathsMatch(string? actualPath, string expectedNormalizedPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath)) return false;
        return string.Equals(NormalizePath(actualPath), expectedNormalizedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string rawPath)
    {
        var trimmed = rawPath.Trim();
        var queryIdx = trimmed.IndexOf('?');
        if (queryIdx >= 0)
        {
            trimmed = trimmed[..queryIdx];
        }

        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
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

        foreach (var kvp in _consumedSessions)
        {
            if (now > kvp.Value.ExpiresAt)
            {
                _consumedSessions.TryRemove(kvp.Key, out _);
            }
        }
    }
}
