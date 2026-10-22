using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Licensing;

/// <summary>
/// Computes this server's relay identity (escrow public key fingerprint) and persists the trial ticket the relay returns.
/// </summary>
public class RelayTrialTicketService
{
    private readonly ServerConfigRepository _serverConfigs;
    private readonly ILogger<RelayTrialTicketService>? _logger;

    private static readonly object FingerprintCacheLock = new();
    private static string? _cachedKeyXml;
    private static string? _cachedFingerprint;

    public RelayTrialTicketService(ServerConfigRepository serverConfigs, ILogger<RelayTrialTicketService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(serverConfigs);
        _serverConfigs = serverConfigs;
        _logger = logger;
    }

    /// <summary>
    /// Base64url SHA-256 of the escrow public key's DER SubjectPublicKeyInfo (the standard public-key fingerprint).
    /// The escrow keypair is generated randomly once per data set and survives master-password changes and backups,
    /// so the fingerprint identifies "this server's data" without exposing anything secret. Returns null if no key exists.
    /// </summary>
    public static string? ComputeFingerprint(string? rsaPublicKeyXml)
    {
        if (string.IsNullOrWhiteSpace(rsaPublicKeyXml)) return null;

        lock (FingerprintCacheLock)
        {
            if (string.Equals(_cachedKeyXml, rsaPublicKeyXml, StringComparison.Ordinal))
            {
                return _cachedFingerprint;
            }
        }

        string? fingerprint;
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(rsaPublicKeyXml);
            fingerprint = WebEncoders.Base64UrlEncode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            fingerprint = null;
        }

        lock (FingerprintCacheLock)
        {
            _cachedKeyXml = rsaPublicKeyXml;
            _cachedFingerprint = fingerprint;
        }
        return fingerprint;
    }

    /// <summary>
    /// The stored ticket, but only if it belongs to the server's current escrow key. A ticket for another key
    /// (e.g. a copied config) is ignored so the server asks the relay again.
    /// </summary>
    public static RelayTrialTicket? GetTicketForCurrentKey(ServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var ticket = config.RelayTrialTicket;
        if (ticket == null || string.IsNullOrEmpty(ticket.Fingerprint)) return null;

        return string.Equals(ticket.Fingerprint, ComputeFingerprint(config.ServerMasterRsaPublicKey), StringComparison.Ordinal)
            ? ticket
            : null;
    }

    public static bool TryGetExpiry(RelayTrialTicket ticket, out DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return DateTime.TryParse(ticket.ExpiresAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out expiresAtUtc);
    }

    /// <summary>
    /// Persists a ticket returned by the relay. Saved immediately (JsonRepository persistence rule).
    /// </summary>
    public virtual void StoreTicket(RelayTrialTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var config = _serverConfigs.GetOrCreateGlobalConfig();

        var currentFingerprint = ComputeFingerprint(config.ServerMasterRsaPublicKey);
        if (!string.Equals(ticket.Fingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            _logger?.LogWarning("Ignoring relay trial ticket for a different server fingerprint.");
            return;
        }

        var existing = config.RelayTrialTicket;
        if (existing != null
            && existing.Fingerprint == ticket.Fingerprint
            && existing.IssuedAt == ticket.IssuedAt
            && existing.ExpiresAt == ticket.ExpiresAt
            && existing.Signature == ticket.Signature)
        {
            return;
        }

        config.RelayTrialTicket = ticket;
        config.LastUpdated = DateTime.UtcNow;
        _serverConfigs.Save(config);
        _logger?.LogInformation("Stored relay trial ticket (expires {ExpiresAt}).", ticket.ExpiresAt);
    }
}
