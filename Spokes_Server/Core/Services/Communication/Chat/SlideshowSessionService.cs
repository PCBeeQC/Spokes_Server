namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

    SlideshowSession CreateSession(string tvConnectionId, SlideshowDisplayKind displayKind = SlideshowDisplayKind.BlazorPinStage, string? creatorIp = null);
    SlideshowSession CreateDirectSession(string hostUserId, string hostConnectionId, string albumId, ISlideshowFileBridge? fileBridge = null);
    SlideshowSession? GetSessionById(string sessionId);
    SlideshowSession? GetSessionByPin(string pin);
    bool TryPair(string pin, string hostUserId, string hostConnectionId, string albumId, out SlideshowSession? session, out string? error, ISlideshowFileBridge? fileBridge = null);
    bool TryBindDirectStageCircuit(string sessionId, string? tvSecret, out string? circuitToken, out SlideshowSession? session);
    bool EndSessionIfStageOwner(string sessionId, string? ownedTvConnectionId, string? boundDirectStageToken);
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
    bool RebindTvConnection(string sessionId, string newConnectionId, string? tvSecret);
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
    private readonly ConcurrentDictionary<string, List<DateTime>> _failedPinAttemptsByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _unpairedCreationLock = new();
    private readonly ILogger<SlideshowSessionService>? _logger;

    private const int MaxFailedAttempts = 5;
    private const int MaxFailedPinAttemptsPerMinute = 5;
    private const int MaxUnpairedSessionsPerIp = 5;
    private const int MaxGlobalUnpairedSessions = 100;
    private const int SessionTtlMinutes = 30;
    private const int PinTtlMinutes = 5;

    public SlideshowSessionService(ILogger<SlideshowSessionService>? logger = null)
    {
        _logger = logger;
    }

    public SlideshowSession CreateSession(string tvConnectionId, SlideshowDisplayKind displayKind = SlideshowDisplayKind.BlazorPinStage, string? creatorIp = null)
    {
        CleanExpiredSessions();

        string? normalizedIp = string.IsNullOrWhiteSpace(creatorIp) ? null : creatorIp.Trim();

        lock (_unpairedCreationLock)
        {
            var unpairedSessions = _sessionsById.Values
                .Where(s => !s.IsPaired)
                .ToList();

            if (unpairedSessions.Count >= MaxGlobalUnpairedSessions)
            {
                _logger?.LogWarning("Rejected unpaired slideshow session creation: global unpaired cap ({MaxGlobal}) reached", MaxGlobalUnpairedSessions);
                throw new InvalidOperationException("Too many active unpaired slideshow sessions. Please try again shortly.");
            }

            if (normalizedIp != null)
            {
                int perIpCount = unpairedSessions.Count(s => string.Equals(s.CreatorIpAddress, normalizedIp, StringComparison.OrdinalIgnoreCase));
                if (perIpCount >= MaxUnpairedSessionsPerIp)
                {
                    _logger?.LogWarning("Rejected unpaired slideshow session creation from IP {Ip}: per-IP cap ({MaxPerIp}) reached", normalizedIp, MaxUnpairedSessionsPerIp);
                    throw new InvalidOperationException("Too many active unpaired slideshow sessions from this address. Please close unused pairing screens or try again shortly.");
                }
            }

            string pin = GenerateUniqueNumericPin();
            var session = new SlideshowSession
            {
                SessionId = Guid.NewGuid().ToString(),
                TvSecret = GenerateTvSecret(),
                DisplayKind = displayKind,
                CreatorIpAddress = normalizedIp,
                PairingPin = pin,
                TvConnectionId = tvConnectionId,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(PinTtlMinutes),
                LastActivityUtc = DateTime.UtcNow,
                IsPaired = false
            };

            _sessionsById[session.SessionId] = session;
            _pinToSessionId[pin] = session.SessionId;

            _logger?.LogInformation("Created new slideshow session {SessionId} ({DisplayKind}) with PIN for TV connection {ConnectionId}", session.SessionId, displayKind, tvConnectionId);
            return session;
        }
    }

    public SlideshowSession CreateDirectSession(string hostUserId, string hostConnectionId, string albumId, ISlideshowFileBridge? fileBridge = null)
    {
        CleanExpiredSessions();

        var session = new SlideshowSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            TvSecret = GenerateTvSecret(),
            DisplayKind = SlideshowDisplayKind.DirectExternalDisplay,
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

        string rateLimitKey = string.IsNullOrWhiteSpace(hostUserId) ? "__anonymous__" : hostUserId.Trim();
        if (IsUserPinRateLimited(rateLimitKey))
        {
            _logger?.LogWarning("Rate-limited slideshow PIN pairing attempt for user {HostUserId}", rateLimitKey);
            error = "Too many failed pairing attempts. Please wait a minute and try again.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            RecordFailedPinAttempt(rateLimitKey);
            error = "Invalid pairing code.";
            return false;
        }

        if (!_pinToSessionId.TryGetValue(normalized, out var sessionId) || !_sessionsById.TryGetValue(sessionId, out var foundSession))
        {
            RecordFailedPinAttempt(rateLimitKey);
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

    public bool TryBindDirectStageCircuit(string sessionId, string? tvSecret, out string? circuitToken, out SlideshowSession? session)
    {
        circuitToken = null;
        session = null;

        var candidate = GetSessionById(sessionId);
        if (candidate == null || !candidate.IsPaired || candidate.DisplayKind != SlideshowDisplayKind.DirectExternalDisplay)
        {
            return false;
        }

        if (!SecretsMatch(candidate.TvSecret, tvSecret))
        {
            return false;
        }

        lock (candidate)
        {
            if (!string.IsNullOrEmpty(candidate.BoundDirectStageToken))
            {
                return false;
            }

            circuitToken = Guid.NewGuid().ToString("N");
            candidate.BoundDirectStageToken = circuitToken;
            candidate.LastActivityUtc = DateTime.UtcNow;
            candidate.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(SessionTtlMinutes);
            session = candidate;
            return true;
        }
    }

    public bool EndSessionIfStageOwner(string sessionId, string? ownedTvConnectionId, string? boundDirectStageToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessionsById.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        bool isOwner = false;
        lock (session)
        {
            if (!string.IsNullOrEmpty(ownedTvConnectionId) &&
                session.DisplayKind == SlideshowDisplayKind.BlazorPinStage &&
                string.Equals(session.TvConnectionId, ownedTvConnectionId, StringComparison.Ordinal))
            {
                isOwner = true;
            }
            else if (!string.IsNullOrEmpty(boundDirectStageToken) &&
                     session.DisplayKind == SlideshowDisplayKind.DirectExternalDisplay &&
                     SecretsMatch(session.BoundDirectStageToken, boundDirectStageToken))
            {
                isOwner = true;
            }
        }

        if (!isOwner)
        {
            return false;
        }

        EndSession(sessionId);
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
        if (session == null || !session.IsPaired || string.IsNullOrEmpty(callerConnectionId)) return false;

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
            session.BoundDirectStageToken = null;
            _logger?.LogInformation("Ended slideshow session {SessionId}", sessionId);
            OnSessionEnded?.Invoke(sessionId);
        }
    }

    public SlideshowSession? HandleDisconnect(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
        {
            return null;
        }

        foreach (var kvp in _sessionsById)
        {
            var session = kvp.Value;
            if (!string.IsNullOrEmpty(session.HostConnectionId) && session.HostConnectionId == connectionId)
            {
                EndSession(session.SessionId);
                return session;
            }
            if (!string.IsNullOrEmpty(session.TvConnectionId) && session.TvConnectionId == connectionId)
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

    public bool RebindTvConnection(string sessionId, string newConnectionId, string? tvSecret)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(newConnectionId) || string.IsNullOrEmpty(tvSecret))
        {
            return false;
        }

        var session = GetSessionById(sessionId);
        if (session == null)
        {
            return false;
        }

        lock (session)
        {
            if (!session.IsPaired ||
                session.DisplayKind != SlideshowDisplayKind.SignalRCastReceiver ||
                !string.IsNullOrEmpty(session.TvConnectionId) ||
                session.HostConnectionId == newConnectionId ||
                !SecretsMatch(session.TvSecret, tvSecret))
            {
                return false;
            }

            session.TvConnectionId = newConnectionId;
            session.LastActivityUtc = DateTime.UtcNow;
            session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(SessionTtlMinutes);
        }

        _logger?.LogInformation("Successfully rebound TV connection {ConnectionId} for session {SessionId}", newConnectionId, sessionId);
        return true;
    }

    public static bool SecretsMatch(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] providedBytes = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    public static string AppendSecretQuery(string url, string? tvSecret)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(tvSecret))
        {
            return url;
        }

        char separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}st={Uri.EscapeDataString(tvSecret)}";
    }

    private bool IsUserPinRateLimited(string rateLimitKey)
    {
        if (!_failedPinAttemptsByUser.TryGetValue(rateLimitKey, out var attempts))
        {
            return false;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        lock (attempts)
        {
            attempts.RemoveAll(ts => ts < cutoff);
            return attempts.Count >= MaxFailedPinAttemptsPerMinute;
        }
    }

    private void RecordFailedPinAttempt(string rateLimitKey)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddMinutes(-1);
        var attempts = _failedPinAttemptsByUser.GetOrAdd(rateLimitKey, _ => []);
        lock (attempts)
        {
            attempts.RemoveAll(ts => ts < cutoff);
            attempts.Add(now);
        }
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

    private string GenerateUniqueNumericPin()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string candidate = GenerateSecureNumericPin();
            if (!_pinToSessionId.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return GenerateSecureNumericPin();
    }

    private static string GenerateSecureNumericPin()
    {
        int value = RandomNumberGenerator.GetInt32(100000, 1000000);
        return value.ToString();
    }

    private static string GenerateTvSecret()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
