using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class AlbumFileKeyResolverTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly AlbumRepository _albums;
    private readonly ChatChannelRepository _channels;
    private readonly EmployeeRepository _employees;
    private readonly ScopedKeystoreService _keystore;
    private readonly Mock<ICryptoService> _mockCrypto;
    private readonly ServerEscrowService _escrow;
    private readonly AlbumService _albumService;
    private readonly AlbumFileKeyResolver _resolver;

    public AlbumFileKeyResolverTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_AlbumKeyResolver_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataDir,
                ["SPOKES_MASTER_PASSWORD"] = "TestMasterPassword#2026"
            })
            .Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _albums = new AlbumRepository(_persistence, config);
        _channels = new ChatChannelRepository(_persistence, config);
        _employees = new EmployeeRepository(_persistence, config);
        _keystore = new ScopedKeystoreService(new EphemeralDataProtectionProvider());
        _mockCrypto = new Mock<ICryptoService>();

        var realCrypto = new CryptoService();
        var companyProfiles = new CompanyProfileRepository(_persistence, config);
        var serverConfigs = new ServerConfigRepository(_persistence, config);
        _escrow = new ServerEscrowService(companyProfiles, serverConfigs, realCrypto, configuration: config);
        _escrow.Initialize();

        _albumService = new AlbumService(_albums, _channels, _mockCrypto.Object, _escrow, _employees);
        _resolver = new AlbumFileKeyResolver(_albums, _albumService, _keystore, _mockCrypto.Object);
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
    public void ResolveAlbumKey_MissingOrUnencryptedAlbum_ReturnsNotEncryptedEvenWhenTokenKeyProvided()
    {
        var user = new Employee { Id = "u1", IsActive = true };

        var missingRes = _resolver.ResolveAlbumKey("missing_alb", user, tokenEncryptionKey: "stale_token_key");
        Assert.False(missingRes.IsEncrypted);
        Assert.True(missingRes.IsSuccess);
        Assert.Null(missingRes.EncryptionKey);

        _albums.Save(new Album { Id = "unenc_alb", OwnerId = "u1", IsEncrypted = false });
        var unencRes = _resolver.ResolveAlbumKey("unenc_alb", user, tokenEncryptionKey: "stale_token_key");
        Assert.False(unencRes.IsEncrypted);
        Assert.True(unencRes.IsSuccess);
        Assert.Null(unencRes.EncryptionKey);
    }

    [Fact]
    public void ResolveAlbumKey_WithTokenKey_RejectsRemovedContributorAndUnsharedChannelMember()
    {
        var owner = new Employee { Id = "u_owner", PublicKey = "owner_pub", EncryptedPrivateKey = "enc" };
        var contrib = new Employee { Id = "u_contrib", PublicKey = "contrib_pub", EncryptedPrivateKey = "enc" };
        var chanViewer = new Employee { Id = "u_viewer", PublicKey = "viewer_pub", EncryptedPrivateKey = "enc" };
        _employees.Save(owner);
        _employees.Save(contrib);
        _employees.Save(chanViewer);

        _channels.Save(new ChatChannel
        {
            Id = "c_shared",
            ChannelType = ChatChannelType.Group,
            ParticipantIds = ["u_owner", "u_viewer"],
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string>
            {
                ["u_owner"] = "cipher_c",
                ["u_viewer"] = "cipher_c"
            }
        });

        var album = new Album
        {
            Id = "alb_live_check",
            OwnerId = "u_owner",
            IsEncrypted = true,
            ContributorUserIds = ["u_contrib"],
            SharedWithChannelIds = ["c_shared"],
            EncryptedAlbumKeys = new Dictionary<string, string>
            {
                ["u_owner"] = "cipher_o",
                ["u_contrib"] = "cipher_contrib",
                ["Channel_c_shared"] = "cipher_chan"
            }
        };
        _albums.Save(album);

        // Initially owner, contributor, and shared-channel member can all resolve with tokenEncryptionKey
        Assert.Equal("tok_key", _resolver.ResolveDownloadKey("alb_live_check", owner, "tok_key").EncryptionKey);
        Assert.Equal("tok_key", _resolver.ResolveDownloadKey("alb_live_check", contrib, "tok_key").EncryptionKey);
        Assert.Equal("tok_key", _resolver.ResolveDownloadKey("alb_live_check", chanViewer, "tok_key").EncryptionKey);

        // After contributor is removed, stale token key must be rejected immediately
        _mockCrypto.Setup(c => c.DecryptRsa("cipher_o", "owner_priv")).Returns("plain_alb_key");
        Assert.True(_albumService.UpdateContributors("alb_live_check", "u_owner", [], "owner_priv").IsSuccess);

        var deniedContrib = _resolver.ResolveDownloadKey("alb_live_check", contrib, "tok_key");
        Assert.False(deniedContrib.IsSuccess);
        Assert.Null(deniedContrib.EncryptionKey);
        Assert.Equal("You do not have the encryption key to view this album.", deniedContrib.ErrorMessage);

        // After channel is unshared, channel viewer's stale token key must also be rejected immediately
        Assert.True(_albumService.UnshareAlbumWithChannel("alb_live_check", "u_owner", "c_shared").IsSuccess);

        var deniedViewer = _resolver.ResolveDownloadKey("alb_live_check", chanViewer, "tok_key");
        Assert.False(deniedViewer.IsSuccess);
        Assert.Null(deniedViewer.EncryptionKey);
        Assert.Equal("You do not have the encryption key to view this album.", deniedViewer.ErrorMessage);

        // Owner still succeeds
        Assert.Equal("tok_key", _resolver.ResolveDownloadKey("alb_live_check", owner, "tok_key").EncryptionKey);
    }

    [Fact]
    public void ResolveAlbumKey_WithTokenKey_AllowsPublicSharedEscrowAlbumAndRejectsDisallowedPrivateEscrow()
    {
        var vaultOwner = new Employee { Id = "u_vault_owner", PublicKey = "owner_pub", EncryptedPrivateKey = "enc" };
        var viewer = new Employee { Id = "u_pub_viewer", IsActive = true };
        _employees.Save(vaultOwner);
        _employees.Save(viewer);

        _channels.Save(new ChatChannel { Id = "c_public_unenc", ChannelType = ChatChannelType.General, IsEncrypted = false });

        // Album shared to an unencrypted public channel legitimately permits Escrow decryption
        _albums.Save(new Album
        {
            Id = "alb_pub_escrow",
            OwnerId = "u_vault_owner",
            IsEncrypted = true,
            SharedWithChannelIds = ["c_public_unenc"],
            EncryptedAlbumKeys = new Dictionary<string, string>
            {
                ["u_vault_owner"] = "cipher_o",
                ["Escrow"] = "cipher_escrow"
            }
        });
        Assert.Equal("tok_key", _resolver.ResolveDownloadKey("alb_pub_escrow", viewer, "tok_key").EncryptionKey);

        // Private album owned by a vault user without any unencrypted channel share must NOT allow Escrow fallback for a contributor missing their own key
        var contribNoKey = new Employee { Id = "u_contrib_nokey", PublicKey = "c_pub", EncryptedPrivateKey = "enc" };
        _employees.Save(contribNoKey);
        _albums.Save(new Album
        {
            Id = "alb_stale_escrow",
            OwnerId = "u_vault_owner",
            IsEncrypted = true,
            ContributorUserIds = ["u_contrib_nokey"],
            EncryptedAlbumKeys = new Dictionary<string, string>
            {
                ["u_vault_owner"] = "cipher_o",
                ["Escrow"] = "cipher_stale_escrow"
            }
        });

        var deniedStaleEscrow = _resolver.ResolveDownloadKey("alb_stale_escrow", contribNoKey, "tok_key");
        Assert.False(deniedStaleEscrow.IsSuccess);
        Assert.Null(deniedStaleEscrow.EncryptionKey);
    }
}
