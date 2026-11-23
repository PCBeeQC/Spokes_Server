using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Spokes_Server.Core.Services.Security;

/// <summary>
/// Issues and validates short-lived, single-use, user- and session-bound OAuth state nonces
/// for external relay callbacks such as Google Drive backup authorization.
/// </summary>
public class OAuthStateNonceService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private sealed record NonceEntry(string EmployeeId, string SessionId, string Purpose, DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, NonceEntry> _nonces = new(StringComparer.Ordinal);

    /// <summary>
    /// Issues a cryptographically random, single-use state nonce bound to the active employee,
    /// browser session, and OAuth purpose.
    /// </summary>
    public string IssueNonce(string employeeId, string sessionId, string purpose, TimeSpan? ttl = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            throw new ArgumentException("Employee ID is required.", nameof(employeeId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(purpose))
            throw new ArgumentException("Purpose is required.", nameof(purpose));

        var now = DateTime.UtcNow;
        PruneExpired(now);

        var bytes = RandomNumberGenerator.GetBytes(32);
        var nonce = WebEncoders.Base64UrlEncode(bytes);
        var lifetime = ttl.HasValue && ttl.Value > TimeSpan.Zero ? ttl.Value : DefaultTtl;

        _nonces[nonce] = new NonceEntry(employeeId, sessionId, purpose, now.Add(lifetime));
        return nonce;
    }

    /// <summary>
    /// Atomically consumes a previously issued nonce if it exists, has not expired,
    /// and matches the caller's employee ID, session ID, and purpose.
    /// </summary>
    public bool TryConsumeNonce(string employeeId, string sessionId, string purpose, string? nonce)
    {
        var now = DateTime.UtcNow;
        PruneExpired(now);

        if (string.IsNullOrWhiteSpace(employeeId) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(purpose) ||
            string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        if (!_nonces.TryRemove(nonce.Trim(), out var entry))
        {
            return false;
        }

        if (now >= entry.ExpiresAtUtc)
        {
            return false;
        }

        return FixedEquals(entry.EmployeeId, employeeId) &&
               FixedEquals(entry.SessionId, sessionId) &&
               FixedEquals(entry.Purpose, purpose);
    }

    private void PruneExpired(DateTime utcNow)
    {
        foreach (var kv in _nonces)
        {
            if (utcNow >= kv.Value.ExpiresAtUtc)
            {
                _nonces.TryRemove(kv.Key, out _);
            }
        }
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
