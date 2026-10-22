namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Communication;

public interface ISlideshowSessionService
{
    event Action<string, SlideshowStagePayload>? OnSessionStarted;
    event Action<string, int>? OnSlideChanged;
    event Action<string, double, double, double, bool>? OnZoomUpdated;
    event Action<string, bool, double, bool, bool>? OnVideoControlUpdated;
    event Action<string, double>? OnTvVideoDurationKnown;
    event Action<string, double, bool, double>? OnTvVideoTimeUpdated;
    event Action<string>? OnTvVideoEnded;
    event Action<string>? OnSessionEnded;

    SlideshowSession CreateSession(string tvConnectionId);
    SlideshowSession CreateDirectSession(string hostUserId, string hostConnectionId, string albumId, ISlideshowFileBridge? fileBridge = null);
    SlideshowSession? GetSessionById(string sessionId);
    SlideshowSession? GetSessionByPin(string pin);
    bool TryPair(string pin, string hostUserId, string hostConnectionId, string albumId, out SlideshowSession? session, out string? error, ISlideshowFileBridge? fileBridge = null);
    void AttachBridge(string sessionId, ISlideshowFileBridge bridge);
    bool ValidateHostCommand(string sessionId, string callerConnectionId, out SlideshowSession? session);
    void UpdateCurrentIndex(string sessionId, int newIndex);
    void NotifySessionStarted(string sessionId, SlideshowStagePayload payload);
    void NotifySlideChanged(string sessionId, int newIndex);
    void NotifyZoomUpdated(string sessionId, double scale, double x, double y, bool isPanning = false);
    void NotifyVideoControlUpdated(string sessionId, bool isPlaying, double currentTime, bool isMuted, bool isLooping = true);
    void NotifyTvVideoDurationKnown(string sessionId, double duration);
    void NotifyTvVideoTimeUpdated(string sessionId, double currentTime, bool isPlaying, double duration);
    void NotifyTvVideoEnded(string sessionId);
    void RecordActivity(string sessionId);
    void EndSession(string sessionId);
    SlideshowSession? HandleDisconnect(string connectionId);
    bool RebindTvConnection(string sessionId, string newConnectionId);
}

public class SlideshowSessionService : ISlideshowSessionService
{
    public event Action<string, SlideshowStagePayload>? OnSessionStarted;
    public event Action<string, int>? OnSlideChanged;
    public event Action<string, double, double, double, bool>? OnZoomUpdated;
    public event Action<string, bool, double, bool, bool>? OnVideoControlUpdated;
    public event Action<string, double>? OnTvVideoDurationKnown;
    public event Action<string, double, bool, double>? OnTvVideoTimeUpdated;
    public event Action<string>? OnTvVideoEnded;
    public event Action<string>? OnSessionEnded;

    private readonly ConcurrentDictionary<string, SlideshowSession> _sessionsById = new();
    private readonly ConcurrentDictionary<string, string> _pinToSessionId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SlideshowSessionService>? _logger;

    private const int MaxFailedAttempts = 5;
    private const int SessionTtlMinutes = 30;
    private const int PinTtlMinutes = 5;

    public SlideshowSessionService(ILogger<SlideshowSessionService>? logger = null)
    {
        _logger = logger;
    }

    public SlideshowSession CreateSession(string tvConnectionId)
    {
        CleanExpiredSessions();

        string pin = GenerateSecureNumericPin();
        var session = new SlideshowSession
        {
            SessionId = Guid.NewGuid().ToString(),
            PairingPin = pin,
            TvConnectionId = tvConnectionId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(PinTtlMinutes),
            LastActivityUtc = DateTime.UtcNow,
            IsPaired = false
        };

        _sessionsById[session.SessionId] = session;
        _pinToSessionId[pin] = session.SessionId;

        _logger?.LogInformation("Created new slideshow session {SessionId} with PIN for TV connection {ConnectionId}", session.SessionId, tvConnectionId);
        return session;
    }

    public SlideshowSession CreateDirectSession(string hostUserId, string hostConnectionId, string albumId, ISlideshowFileBridge? fileBridge = null)
    {
        CleanExpiredSessions();

        var session = new SlideshowSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PairingPin = string.Empty,
            TvConnectionId = string.Empty,
            HostUserId = hostUserId,
            HostConnectionId = hostConnectionId,
            AlbumId = albumId,
            FileBridge = fileBridge,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(SessionTtlMinutes),
            LastActivityUtc = DateTime.UtcNow,
            IsPaired = true
        };

        _sessionsById[session.SessionId] = session;
        _logger?.LogInformation("Created direct paired slideshow session {SessionId} for album {AlbumId} by host user {HostUserId}", session.SessionId, albumId, hostUserId);
        return session;
    }

    public SlideshowSession? GetSessionById(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        if (_sessionsById.TryGetValue(sessionId, out var session))
        {
            if (session.ExpiresAtUtc < DateTime.UtcNow)
            {
                EndSession(sessionId);
                return null;
            }
            return session;
        }
        return null;
    }

    public SlideshowSession? GetSessionByPin(string pin)
    {
        string normalized = NormalizePin(pin);
        if (_pinToSessionId.TryGetValue(normalized, out var sessionId))
        {
            return GetSessionById(sessionId);
        }
        return null;
    }

    public bool TryPair(string pin, string hostUserId, string hostConnectionId, string albumId, out SlideshowSession? session, out string? error, ISlideshowFileBridge? fileBridge = null)
    {
        string normalized = NormalizePin(pin);
        session = null;
        error = null;

        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Invalid pairing code.";
            return false;
        }

        if (!_pinToSessionId.TryGetValue(normalized, out var sessionId) || !_sessionsById.TryGetValue(sessionId, out var foundSession))
        {
            error = "Invalid or expired pairing code.";
            return false;
        }

        lock (foundSession)
        {
            if (foundSession.ExpiresAtUtc < DateTime.UtcNow)
            {
                EndSession(sessionId);
                error = "Pairing code has expired. Please refresh the TV screen.";
                return false;
            }

            if (foundSession.FailedAttempts >= MaxFailedAttempts)
            {
                EndSession(sessionId);
                error = "Too many failed attempts. This pairing code has been locked.";
                return false;
            }

            if (foundSession.IsPaired)
            {
                error = "This session is already paired with another host.";
                return false;
            }

            foundSession.IsPaired = true;
            foundSession.HostUserId = hostUserId;
            foundSession.HostConnectionId = hostConnectionId;
            foundSession.AlbumId = albumId;
            foundSession.FileBridge = fileBridge;
            foundSession.LastActivityUtc = DateTime.UtcNow;
            foundSession.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(SessionTtlMinutes);

            // Consume the PIN so it cannot be reused
            _pinToSessionId.TryRemove(normalized, out _);
            session = foundSession;
        }

        _logger?.LogInformation("Successfully paired slideshow session {SessionId} with host user {HostUserId}", sessionId, hostUserId);
        return true;
    }

    public void AttachBridge(string sessionId, ISlideshowFileBridge bridge)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || bridge == null) return;
        if (_sessionsById.TryGetValue(sessionId, out var session))
        {
            session.FileBridge = bridge;
        }
    }

    public bool ValidateHostCommand(string sessionId, string callerConnectionId, out SlideshowSession? session)
    {
        session = GetSessionById(sessionId);
        if (session == null || !session.IsPaired) return false;

        if (session.HostConnectionId != callerConnectionId)
        {
            _logger?.LogWarning("Unauthorized host command attempt on session {SessionId} from connection {CallerId}", sessionId, callerConnectionId);
            return false;
        }

        session.LastActivityUtc = DateTime.UtcNow;
        session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(SessionTtlMinutes);
        return true;
    }

    public void UpdateCurrentIndex(string sessionId, int newIndex)
    {
        if (_sessionsById.TryGetValue(sessionId, out var session))
        {
            session.CurrentIndex = Math.Max(0, newIndex);
            session.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public void NotifySessionStarted(string sessionId, SlideshowStagePayload payload)
    {
        if (_sessionsById.TryGetValue(sessionId, out var session))
        {
            session.StagePayload = payload;
            session.CurrentIndex = payload.InitialIndex;
            session.LastActivityUtc = DateTime.UtcNow;
            session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(SessionTtlMinutes);
        }
        OnSessionStarted?.Invoke(sessionId, payload);
    }

    public void NotifySlideChanged(string sessionId, int newIndex)
    {
        UpdateCurrentIndex(sessionId, newIndex);
        OnSlideChanged?.Invoke(sessionId, newIndex);
    }

    public void NotifyZoomUpdated(string sessionId, double scale, double x, double y, bool isPanning = false)
    {
        RecordActivity(sessionId);
        OnZoomUpdated?.Invoke(sessionId, scale, x, y, isPanning);
    }

    public void NotifyVideoControlUpdated(string sessionId, bool isPlaying, double currentTime, bool isMuted, bool isLooping = true)
    {
        RecordActivity(sessionId);
        OnVideoControlUpdated?.Invoke(sessionId, isPlaying, currentTime, isMuted, isLooping);
    }

    public void NotifyTvVideoDurationKnown(string sessionId, double duration)
    {
        RecordActivity(sessionId);
        OnTvVideoDurationKnown?.Invoke(sessionId, duration);
    }

    public void NotifyTvVideoTimeUpdated(string sessionId, double currentTime, bool isPlaying, double duration)
    {
        RecordActivity(sessionId);
        OnTvVideoTimeUpdated?.Invoke(sessionId, currentTime, isPlaying, duration);
    }

    public void NotifyTvVideoEnded(string sessionId)
    {
        RecordActivity(sessionId);
        OnTvVideoEnded?.Invoke(sessionId);
    }

    public void RecordActivity(string sessionId)
    {
        if (_sessionsById.TryGetValue(sessionId, out var session))
        {
            session.LastActivityUtc = DateTime.UtcNow;
            session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(SessionTtlMinutes);
        }
    }

    public void EndSession(string sessionId)
    {
        if (_sessionsById.TryRemove(sessionId, out var session))
        {
            _pinToSessionId.TryRemove(session.PairingPin, out _);
            session.FileBridge = null;
            _logger?.LogInformation("Ended slideshow session {SessionId}", sessionId);
            OnSessionEnded?.Invoke(sessionId);
        }
    }

    public SlideshowSession? HandleDisconnect(string connectionId)
    {
        foreach (var kvp in _sessionsById)
        {
            var session = kvp.Value;
            if (session.HostConnectionId == connectionId)
            {
                EndSession(session.SessionId);
                return session;
            }
            if (session.TvConnectionId == connectionId)
            {
                if (!session.IsPaired)
                {
                    EndSession(session.SessionId);
                    return session;
                }
                else
                {
                    session.TvConnectionId = string.Empty;
                    _logger?.LogInformation("TV connection {ConnectionId} disconnected from paired session {SessionId}; keeping session alive for reconnect", connectionId, session.SessionId);
                    return null;
                }
            }
        }
        return null;
    }

    public bool RebindTvConnection(string sessionId, string newConnectionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(newConnectionId))
        {
            return false;
        }

        var session = GetSessionById(sessionId);
        if (session == null || !session.IsPaired)
        {
            return false;
        }

        session.TvConnectionId = newConnectionId;
        session.LastActivityUtc = DateTime.UtcNow;
        session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(SessionTtlMinutes);
        _logger?.LogInformation("Successfully rebound TV connection {ConnectionId} for session {SessionId}", newConnectionId, sessionId);
        return true;
    }

    private void CleanExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _sessionsById)
        {
            if (kvp.Value.ExpiresAtUtc < now)
            {
                EndSession(kvp.Key);
            }
        }
    }

    public static string NormalizePin(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return string.Empty;
        return new string(pin.Where(char.IsDigit).ToArray());
    }

    private static string GenerateSecureNumericPin()
    {
        int value = RandomNumberGenerator.GetInt32(100000, 1000000);
        return value.ToString();
    }
}
