using System;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data.Repositories.Core;

using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Services.Security;

public class ServerEscrowService
{
    private readonly Database _db;
    private readonly ICryptoService _crypto;
    private string? _masterPassword;

    public string? EscrowPublicKey { get; private set; }
    public string? DecryptedEscrowPrivateKey { get; private set; }

    public bool IsEscrowAvailable => !string.IsNullOrEmpty(EscrowPublicKey) && !string.IsNullOrEmpty(DecryptedEscrowPrivateKey);

    public ServerEscrowService(Database db, ICryptoService crypto)
    {
        _db = db;
        _crypto = crypto;
    }

    public void Initialize()
    {
        var profile = _db.CompanyProfile.Get();
        var envPassword = Environment.GetEnvironmentVariable("SPOKES_MASTER_PASSWORD");
        var jsonPassword = profile.PublicChannelMasterPassword;

        _masterPassword = !string.IsNullOrEmpty(envPassword) ? envPassword : jsonPassword;

        InitializeEscrow(envPassword, jsonPassword, profile);
    }

    private void InitializeEscrow(string? envPassword, string? jsonPassword, Spokes_Server.Core.Models.Core.CompanyProfile profile)
    {
        if (string.IsNullOrEmpty(_masterPassword))
        {
            return;
        }

        var config = _db.ServerConfigs.GetOrCreateGlobalConfig();

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
                        var oldKek = _crypto.DeriveKeyFromPassword(jsonPassword, "ServerEscrowSalt_2024", 100000);
                        var privKey = _crypto.DecryptAes(config.ServerMasterEncryptedPrivateKey, oldKek);

                        var newKek = _crypto.DeriveKeyFromPassword(envPassword, "ServerEscrowSalt_2024", 100000);
                        config.ServerMasterEncryptedPrivateKey = _crypto.EncryptAes(privKey, newKek);
                        _db.ServerConfigs.Save(config);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Escrow] Failed to migrate escrow key: {ex.Message}");
                        // We must return here so we don't wipe the legacy password if decryption fails
                        return;
                    }
                }
            }

            // Wipe the json password from the database to secure the system
            profile.PublicChannelMasterPassword = string.Empty;
            _db.CompanyProfile.Save(profile);
        }

        // Use a static salt for the server master key derivation
        var kek = _crypto.DeriveKeyFromPassword(_masterPassword, "ServerEscrowSalt_2024", 100000);

        if (string.IsNullOrEmpty(config.ServerMasterRsaPublicKey) || string.IsNullOrEmpty(config.ServerMasterEncryptedPrivateKey))
        {
            // First time setup: Generate Escrow RSA Key pair
            var (pub, priv) = _crypto.GenerateRsaKeyPair();

            config.ServerMasterRsaPublicKey = pub;
            config.ServerMasterEncryptedPrivateKey = _crypto.EncryptAes(priv, kek);
            _db.ServerConfigs.Save(config);

            EscrowPublicKey = pub;
            DecryptedEscrowPrivateKey = priv;
        }
        else
        {
            // Load existing Escrow
            EscrowPublicKey = config.ServerMasterRsaPublicKey;
            try
            {
                DecryptedEscrowPrivateKey = _crypto.DecryptAes(config.ServerMasterEncryptedPrivateKey, kek);
            }
            catch
            {
                // Invalid password
                DecryptedEscrowPrivateKey = null;
            }
        }
    }
}
