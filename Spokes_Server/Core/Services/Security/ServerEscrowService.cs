using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Security;

public class ServerEscrowService
{
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly ICryptoService _crypto;
    private readonly ILogger<ServerEscrowService>? _logger;
    private readonly IConfiguration? _configuration;
    private readonly object _initLock = new();
    private bool _isInitialized;
    private string? _masterPassword;
    private string? _escrowPublicKey;
    private string? _decryptedEscrowPrivateKey;

    public string? EscrowPublicKey
    {
        get
        {
            EnsureInitialized();
            return _escrowPublicKey;
        }
        internal set
        {
            lock (_initLock)
            {
                _escrowPublicKey = value;
                _isInitialized = true;
            }
        }
    }

    public string? DecryptedEscrowPrivateKey
    {
        get
        {
            EnsureInitialized();
            return _decryptedEscrowPrivateKey;
        }
        internal set
        {
            lock (_initLock)
            {
                _decryptedEscrowPrivateKey = value;
                _isInitialized = true;
            }
        }
    }

    public bool IsEscrowAvailable
    {
        get
        {
            EnsureInitialized();
            return !string.IsNullOrEmpty(_escrowPublicKey) && !string.IsNullOrEmpty(_decryptedEscrowPrivateKey);
        }
    }

    public ServerEscrowService(
        CompanyProfileRepository companyProfiles,
        ServerConfigRepository serverConfigs,
        ICryptoService crypto,
        ILogger<ServerEscrowService>? logger = null,
        IConfiguration? configuration = null)
    {
        _companyProfiles = companyProfiles ?? throw new ArgumentNullException(nameof(companyProfiles));
        _serverConfigs = serverConfigs ?? throw new ArgumentNullException(nameof(serverConfigs));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Explicitly initializes escrow state (e.g. during server startup pre-warming).
    /// Accessing escrow properties also lazily initializes keys on-demand.
    /// </summary>
    public void Initialize()
    {
        lock (_initLock)
        {
            InitializeEscrowInternal();
            _isInitialized = true;
        }
    }

    private void EnsureInitialized()
    {
        if (_isInitialized) return;
        lock (_initLock)
        {
            if (_isInitialized) return;
            InitializeEscrowInternal();
            _isInitialized = true;
        }
    }

    private void InitializeEscrowInternal()
    {
        var profile = _companyProfiles.Get();
        var envPassword = _configuration != null ? _configuration["SPOKES_MASTER_PASSWORD"] : Environment.GetEnvironmentVariable("SPOKES_MASTER_PASSWORD");
        var jsonPassword = profile.PublicChannelMasterPassword;

        _masterPassword = !string.IsNullOrEmpty(envPassword) ? envPassword : jsonPassword;

        InitializeEscrow(envPassword, jsonPassword, profile);
    }

    /// <summary>
    /// Gets the decrypted escrow private key, throwing an InvalidOperationException if escrow is uninitialized or unavailable.
    /// </summary>
    public string GetRequiredDecryptedPrivateKey()
    {
        EnsureInitialized();
        if (string.IsNullOrEmpty(_decryptedEscrowPrivateKey))
        {
            throw new InvalidOperationException("Server escrow private key is not initialized or unavailable.");
        }
        return _decryptedEscrowPrivateKey;
    }

    /// <summary>
    /// Gets the escrow RSA public key, throwing an InvalidOperationException if escrow is uninitialized or unavailable.
    /// </summary>
    public string GetRequiredPublicKey()
    {
        EnsureInitialized();
        if (string.IsNullOrEmpty(_escrowPublicKey))
        {
            throw new InvalidOperationException("Server escrow public key is not initialized or unavailable.");
        }
        return _escrowPublicKey;
    }

    private void InitializeEscrow(string? envPassword, string? jsonPassword, CompanyProfile profile)
    {
        if (string.IsNullOrEmpty(_masterPassword))
        {
            return;
        }

        var config = _serverConfigs.GetOrCreateGlobalConfig();
        const string legacySalt = "ServerEscrowSalt_2024";

        // Handle Migration
        if (!string.IsNullOrEmpty(envPassword) && !string.IsNullOrEmpty(jsonPassword))
        {
            if (envPassword != jsonPassword)
            {
                // The password changed. Decrypt with old, re-encrypt with new.
                if (!string.IsNullOrEmpty(config.ServerMasterEncryptedPrivateKey))
                {
                    try
                    {
                        var oldSalt = !string.IsNullOrEmpty(config.ServerMasterKeySalt) ? config.ServerMasterKeySalt : legacySalt;
                        var oldKek = _crypto.DeriveKeyFromPassword(jsonPassword, oldSalt, 100000);
                        var privKey = _crypto.DecryptAes(config.ServerMasterEncryptedPrivateKey, oldKek);

                        var newSalt = Guid.NewGuid().ToString("N");
                        var newKek = _crypto.DeriveKeyFromPassword(envPassword, newSalt, 100000);
                        config.ServerMasterEncryptedPrivateKey = _crypto.EncryptAes(privKey, newKek);
                        config.ServerMasterKeySalt = newSalt;
                        _serverConfigs.Save(config);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "[Escrow] Failed to migrate escrow key: {Message}", ex.Message);
                        // We must return here so we don't wipe the legacy password if decryption fails
                        return;
                    }
                }
            }

            // Wipe the json password from the database to secure the system
            profile.PublicChannelMasterPassword = string.Empty;
            _companyProfiles.Save(profile);
        }

        if (string.IsNullOrEmpty(config.ServerMasterRsaPublicKey) || string.IsNullOrEmpty(config.ServerMasterEncryptedPrivateKey))
        {
            // First time setup: Generate Escrow RSA Key pair with unique salt
            var uniqueSalt = Guid.NewGuid().ToString("N");
            var kek = _crypto.DeriveKeyFromPassword(_masterPassword, uniqueSalt, 100000);

            var (pub, priv) = _crypto.GenerateRsaKeyPair();

            config.ServerMasterRsaPublicKey = pub;
            config.ServerMasterEncryptedPrivateKey = _crypto.EncryptAes(priv, kek);
            config.ServerMasterKeySalt = uniqueSalt;
            _serverConfigs.Save(config);

            _escrowPublicKey = pub;
            _decryptedEscrowPrivateKey = priv;
        }
        else
        {
            // Load existing Escrow
            _escrowPublicKey = config.ServerMasterRsaPublicKey;
            var currentSalt = !string.IsNullOrEmpty(config.ServerMasterKeySalt) ? config.ServerMasterKeySalt : legacySalt;
            var kek = _crypto.DeriveKeyFromPassword(_masterPassword, currentSalt, 100000);

            try
            {
                _decryptedEscrowPrivateKey = _crypto.DecryptAes(config.ServerMasterEncryptedPrivateKey, kek);

                // Auto-upgrade legacy static salt if config has empty ServerMasterKeySalt
                if (string.IsNullOrEmpty(config.ServerMasterKeySalt) && !string.IsNullOrEmpty(_decryptedEscrowPrivateKey))
                {
                    try
                    {
                        var newSalt = Guid.NewGuid().ToString("N");
                        var newKek = _crypto.DeriveKeyFromPassword(_masterPassword, newSalt, 100000);
                        config.ServerMasterEncryptedPrivateKey = _crypto.EncryptAes(_decryptedEscrowPrivateKey, newKek);
                        config.ServerMasterKeySalt = newSalt;
                        _serverConfigs.Save(config);
                        _logger?.LogInformation("[Escrow] Successfully upgraded server escrow key to unique per-instance salt.");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "[Escrow] Failed to upgrade escrow salt: {Message}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                // Invalid password
                _logger?.LogWarning(ex, "[Escrow] Failed to decrypt server master private key. Invalid master password or corrupted key.");
                _decryptedEscrowPrivateKey = null;
            }
        }
    }
}
