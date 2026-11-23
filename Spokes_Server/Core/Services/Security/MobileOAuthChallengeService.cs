using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Spokes_Server.Core.Services.Security;

public readonly record struct MobileOAuthChallenge(string State, string Nonce);

/// <summary>
/// Issues and validates short-lived, single-use, device-bound OAuth state/nonce challenges
/// for the native mobile PKCE flow so arbitrary callbacks or cross-device codes cannot be replayed.
/// </summary>
public class MobileOAuthChallengeService
{
    public const int MaxPendingChallenges = 10_000;
    public const int MaxDeviceIdLength = 256;
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(10);

    private sealed record ChallengeEntry(byte[] DeviceIdSha256, string Nonce, DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, ChallengeEntry> _challenges = new(StringComparer.Ordinal);
    private readonly object _capacityLock = new();

    public MobileOAuthChallengeService()
    {
    }

    public MobileOAuthChallenge CreateChallenge(string deviceId)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        StoreChallenge(normalizedDeviceId, state, nonce);
        return new MobileOAuthChallenge(state, nonce);
    }

    internal string SeedChallengeForTesting(string deviceId, string fixedNonce)
    {
        if (string.IsNullOrWhiteSpace(fixedNonce))
            throw new ArgumentException("Nonce is required.", nameof(fixedNonce));

        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        StoreChallenge(normalizedDeviceId, state, fixedNonce);
        return state;
    }

    public bool TryConsumeChallenge(string? state, string? deviceId, out string expectedNonce)
    {
        expectedNonce = string.Empty;

        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        var trimmedDeviceId = deviceId.Trim();
        if (trimmedDeviceId.Length > MaxDeviceIdLength)
        {
            return false;
        }

        if (!_challenges.TryRemove(state.Trim(), out var entry))
        {
            return false;
        }

        if (DateTime.UtcNow > entry.ExpiresAtUtc)
        {
            return false;
        }

        var deviceHash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmedDeviceId));
        if (!CryptographicOperations.FixedTimeEquals(deviceHash, entry.DeviceIdSha256))
        {
            return false;
        }

        expectedNonce = entry.Nonce;
        return true;
    }

    private void StoreChallenge(string normalizedDeviceId, string state, string nonce)
    {
        var now = DateTime.UtcNow;
        PruneExpired(now);

        if (_challenges.Count >= MaxPendingChallenges)
        {
            lock (_capacityLock)
            {
                PruneExpired(now);
                if (_challenges.Count >= MaxPendingChallenges)
                {
                    var overflow = _challenges.Count - MaxPendingChallenges + 1;
                    var oldestKeys = _challenges
                        .OrderBy(kv => kv.Value.ExpiresAtUtc)
                        .Take(overflow)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var key in oldestKeys)
                    {
                        _challenges.TryRemove(key, out _);
                    }
                }
            }
        }

        var deviceHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDeviceId));
        _challenges[state] = new ChallengeEntry(deviceHash, nonce, now.Add(ChallengeTtl));
    }

    private void PruneExpired(DateTime utcNow)
    {
        foreach (var kv in _challenges)
        {
            if (utcNow > kv.Value.ExpiresAtUtc)
            {
                _challenges.TryRemove(kv.Key, out _);
            }
        }
    }

    private static string NormalizeDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("DeviceId is required.", nameof(deviceId));

        var trimmed = deviceId.Trim();
        if (trimmed.Length > MaxDeviceIdLength)
            throw new ArgumentException($"DeviceId cannot exceed {MaxDeviceIdLength} characters.", nameof(deviceId));

        return trimmed;
    }
}
