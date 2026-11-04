using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class ChatFileKeyResolverTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly ChatChannelRepository _channels;
    private readonly ScopedKeystoreService _keystore;
    private readonly Mock<ICryptoService> _mockCrypto;
    private readonly ChatFileKeyResolver _resolver;

    public ChatFileKeyResolverTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ChatKeyResolver_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _testDataDir })
            .Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _channels = new ChatChannelRepository(_persistence, config);
        _keystore = new ScopedKeystoreService(new EphemeralDataProtectionProvider());
        _mockCrypto = new Mock<ICryptoService>();
        _resolver = new ChatFileKeyResolver(_channels, _keystore, _mockCrypto.Object);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void ResolveChannelKey_MissingOrUnencryptedChannel_ReturnsNotEncryptedEvenWhenTokenKeyProvided()
    {
        var user = new Employee { Id = "u1", IsActive = true };

        var missingRes = _resolver.ResolveChannelKey("missing_chan", user, tokenEncryptionKey: "stale_token_key");
        Assert.False(missingRes.IsEncrypted);
        Assert.True(missingRes.IsSuccess);
        Assert.Null(missingRes.EncryptionKey);

        _channels.Save(new ChatChannel { Id = "unenc_chan", IsEncrypted = false });
        var unencRes = _resolver.ResolveChannelKey("unenc_chan", user, tokenEncryptionKey: "stale_token_key");
        Assert.False(unencRes.IsEncrypted);
        Assert.True(unencRes.IsSuccess);
        Assert.Null(unencRes.EncryptionKey);
    }

    [Fact]
    public void ResolveChannelKey_WithTokenKey_RequiresLiveUserKeyOrVaultlessServerMaster()
    {
        _channels.Save(new ChatChannel
        {
            Id = "enc_chan",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string>
            {
                ["u_member"] = "cipher_member",
                ["ServerMaster"] = "cipher_master"
            }
        });

        var activeMember = new Employee { Id = "u_member", EncryptedPrivateKey = "enc_priv" };
        var okMember = _resolver.ResolveDownloadKey("enc_chan", activeMember, tokenEncryptionKey: "embedded_aes_key");
        Assert.True(okMember.IsSuccess);
        Assert.Equal("embedded_aes_key", okMember.EncryptionKey);

        // Vaultless user is allowed when ServerMaster key exists on the channel
        var vaultlessMember = new Employee { Id = "u_vaultless", EncryptedPrivateKey = null };
        var okVaultless = _resolver.ResolveDownloadKey("enc_chan", vaultlessMember, tokenEncryptionKey: "embedded_aes_key");
        Assert.True(okVaultless.IsSuccess);
        Assert.Equal("embedded_aes_key", okVaultless.EncryptionKey);

        // Removed member who has a vault password must NOT bypass live key check via stale token or ServerMaster
        var removedVaultUser = new Employee { Id = "u_removed", EncryptedPrivateKey = "enc_priv" };
        var deniedRemoved = _resolver.ResolveDownloadKey("enc_chan", removedVaultUser, tokenEncryptionKey: "embedded_aes_key");
        Assert.False(deniedRemoved.IsSuccess);
        Assert.Null(deniedRemoved.EncryptionKey);
        Assert.Equal("You do not have the encryption key for this channel.", deniedRemoved.ErrorMessage);
    }

    [Fact]
    public void ResolveChannelKey_WithoutTokenKey_DecryptsFromUnlockedKeystoreAndFailsWhenLocked()
    {
        _channels.Save(new ChatChannel
        {
            Id = "enc_chan_ks",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["u1"] = "rsa_cipher" }
        });

        var user = new Employee { Id = "u1", EncryptedPrivateKey = "enc_priv" };

        var lockedRes = _resolver.ResolveChannelKey("enc_chan_ks", user);
        Assert.False(lockedRes.IsSuccess);
        Assert.Equal("Your chat vault is locked.", lockedRes.ErrorMessage);

        _keystore.Unlock("plain_priv_key");
        _mockCrypto.Setup(c => c.DecryptRsa("rsa_cipher", "plain_priv_key")).Returns("resolved_aes_key");

        var unlockedRes = _resolver.ResolveChannelKey("enc_chan_ks", user);
        Assert.True(unlockedRes.IsSuccess);
        Assert.Equal("resolved_aes_key", unlockedRes.EncryptionKey);
    }
}
