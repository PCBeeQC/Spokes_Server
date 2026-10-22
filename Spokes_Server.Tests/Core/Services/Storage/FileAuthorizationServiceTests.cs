using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Storage;

public class FileAuthorizationServiceTests : TestDataTestBase
{
    private readonly AlbumRepository _albums;
    private readonly ChatChannelRepository _channels;
    private readonly EmployeeRepository _employees;
    private readonly ScopedKeystoreService _keystore;
    private readonly Mock<ICryptoService> _mockCrypto;
    private readonly AlbumService _albumService;
    private readonly FileAuthorizationService _service;
    private readonly RSA _rsa;
    private readonly string _publicKeyXml;
    private readonly string _privateKeyXml;

    public FileAuthorizationServiceTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "DataPath", _testDataPath }
        }).Build();

        var writer = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        _albums = new AlbumRepository(writer, config);
        _channels = new ChatChannelRepository(writer, config);
        _employees = new EmployeeRepository(writer, config);

        var dataProtection = new EphemeralDataProtectionProvider();
        _keystore = new ScopedKeystoreService(dataProtection);
        _mockCrypto = new Mock<ICryptoService>();

        _rsa = RSA.Create(2048);
        _publicKeyXml = _rsa.ToXmlString(false);
        _privateKeyXml = _rsa.ToXmlString(true);

        _keystore.Unlock(_privateKeyXml);

        var testDb = TestDatabaseFactory.Create();
        var escrow = new ServerEscrowService(testDb.CompanyProfile, testDb.ServerConfigs, _mockCrypto.Object);
        _albumService = new AlbumService(_albums, _channels, _mockCrypto.Object, escrow, _employees);

        _service = CreateService();
    }

    private FileAuthorizationService CreateService(ScopedKeystoreService? keystore = null, IEnumerable<IFileAccessProvider>? providers = null)
    {
        var ks = keystore ?? _keystore;
        var resolvers = new IFileKeyResolver[]
        {
            new ChatFileKeyResolver(_channels, ks, _mockCrypto.Object),
            new AlbumFileKeyResolver(_albums, _albumService, ks, _mockCrypto.Object)
        };
        return new FileAuthorizationService(providers, resolvers);
    }

    [Fact]
    public void ResolveAlbumKey_WhenTokenKeyProvided_ReturnsTokenKeyImmediately()
    {
        var result = _service.ResolveAlbumKey("alb1", new Employee { Id = "user1" }, "custom_token_key");
        Assert.True(result.IsSuccess);
        Assert.Equal("custom_token_key", result.EncryptionKey);
    }

    [Fact]
    public void ResolveAlbumKey_WhenAlbumNotEncrypted_ReturnsNotEncrypted()
    {
        var album = new Album { Id = "alb_unenc", IsEncrypted = false };
        _albums.Save(album);

        var result = _service.ResolveAlbumKey("alb_unenc", new Employee { Id = "user1" });
        Assert.False(result.IsEncrypted);
        Assert.Null(result.EncryptionKey);
    }

    [Fact]
    public void ResolveAlbumKey_WhenAlbumEncryptedAndUserHasKey_ReturnsDecryptedKey()
    {
        var album = new Album
        {
            Id = "alb_enc",
            IsEncrypted = true,
            EncryptedAlbumKeys = new Dictionary<string, string> { { "user1", "encrypted_album_key" } }
        };
        _albums.Save(album);

        _mockCrypto.Setup(c => c.DecryptRsa("encrypted_album_key", _privateKeyXml))
            .Returns("plain_aes_key");

        var result = _service.ResolveAlbumKey("alb_enc", new Employee { Id = "user1" });
        Assert.True(result.IsSuccess);
        Assert.Equal("plain_aes_key", result.EncryptionKey);
    }

    [Fact]
    public void ResolveAlbumKey_WhenVaultLocked_ReturnsLockedReason()
    {
        var lockedKeystore = new ScopedKeystoreService(new EphemeralDataProtectionProvider());
        var serviceWithLockedKeystore = CreateService(lockedKeystore);

        var album = new Album
        {
            Id = "alb_enc_locked",
            IsEncrypted = true,
            EncryptedAlbumKeys = new Dictionary<string, string> { { "user1", "encrypted_key" } }
        };
        _albums.Save(album);

        var result = serviceWithLockedKeystore.ResolveDownloadKey("albums", "alb_enc_locked", new Employee { Id = "user1" });
        Assert.False(result.IsSuccess);
        Assert.Equal("Your Secure Vault is locked. Unlock your vault to access this album.", result.ErrorMessage);
    }

    [Fact]
    public void ResolveChannelKey_WhenTokenKeyProvided_ReturnsTokenKey()
    {
        var user = new Employee { Id = "user1" };
        var result = _service.ResolveChannelKey("chan1", user, "token_key");

        Assert.True(result.IsSuccess);
        Assert.Equal("token_key", result.EncryptionKey);
    }

    [Fact]
    public void ResolveChannelKey_WhenChannelNotEncrypted_ReturnsNotEncrypted()
    {
        var channel = new ChatChannel { Id = "chan_unenc", IsEncrypted = false };
        _channels.Save(channel);
        var user = new Employee { Id = "user1" };

        var result = _service.ResolveChannelKey("chan_unenc", user);
        Assert.False(result.IsEncrypted);
        Assert.Null(result.EncryptionKey);
    }

    [Fact]
    public void ResolveChannelKey_WhenVaultLocked_ReturnsLockedReason()
    {
        var lockedKeystore = new ScopedKeystoreService(new EphemeralDataProtectionProvider());
        var serviceWithLockedKeystore = CreateService(lockedKeystore);

        var channel = new ChatChannel { Id = "chan_enc", IsEncrypted = true };
        _channels.Save(channel);
        var user = new Employee { Id = "user1" };

        var result = serviceWithLockedKeystore.ResolveChannelKey("chan_enc", user);
        Assert.False(result.IsSuccess);
        Assert.Equal("You do not have the encryption key for this channel.", result.ErrorMessage);
    }

    [Fact]
    public void ResolveChannelKey_WhenUserMissingChannelKey_ReturnsMissingKeyReason()
    {
        var channel = new ChatChannel { Id = "chan_enc_nokey", IsEncrypted = true };
        _channels.Save(channel);
        var user = new Employee { Id = "user1" };

        var result = _service.ResolveChannelKey("chan_enc_nokey", user);
        Assert.False(result.IsSuccess);
        Assert.Equal("You do not have the encryption key for this channel.", result.ErrorMessage);
    }

    [Fact]
    public void ResolveChannelKey_WhenValid_ReturnsDecryptedChannelKey()
    {
        var channel = new ChatChannel
        {
            Id = "chan_valid",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { { "user1", "enc_chan_key" } }
        };
        _channels.Save(channel);
        var user = new Employee { Id = "user1" };

        _mockCrypto.Setup(c => c.DecryptRsa("enc_chan_key", _privateKeyXml))
            .Returns("plain_chan_aes");

        var result = _service.ResolveChannelKey("chan_valid", user);
        Assert.True(result.IsSuccess);
        Assert.Equal("plain_chan_aes", result.EncryptionKey);
    }

    [Fact]
    public void ResolveUploadKey_ForChat_ResolvesSuccessOrFailure()
    {
        var channel = new ChatChannel
        {
            Id = "chan_up",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { { "user1", "enc_key" } }
        };
        _channels.Save(channel);
        var user = new Employee { Id = "user1" };

        _mockCrypto.Setup(c => c.DecryptRsa("enc_key", _privateKeyXml)).Returns("plain_key");

        var success = _service.ResolveUploadKey("chat", "chan_up", user);
        Assert.True(success.IsSuccess);
        Assert.True(success.IsEncrypted);
        Assert.Equal("plain_key", success.EncryptionKey);

        var missingUser = new Employee { Id = "user_missing" };
        var failure = _service.ResolveUploadKey("chat", "chan_up", missingUser);
        Assert.False(failure.IsSuccess);
        Assert.Contains("missing token", failure.ErrorMessage);
    }

    [Fact]
    public void ResolveDownloadKey_ForAlbums_ResolvesSuccessOrFailure()
    {
        var album = new Album
        {
            Id = "alb_down",
            IsEncrypted = true,
            EncryptedAlbumKeys = new Dictionary<string, string> { { "user1", "enc_alb_key" } }
        };
        _albums.Save(album);
        var user = new Employee { Id = "user1" };

        _mockCrypto.Setup(c => c.DecryptRsa("enc_alb_key", _privateKeyXml)).Returns("plain_alb_key");

        var success = _service.ResolveDownloadKey("albums", "alb_down", user);
        Assert.True(success.IsSuccess);
        Assert.True(success.IsEncrypted);
        Assert.Equal("plain_alb_key", success.EncryptionKey);

        var stranger = new Employee { Id = "stranger" };
        var failure = _service.ResolveDownloadKey("albums", "alb_down", stranger);
        Assert.False(failure.IsSuccess);
        Assert.Contains("do not have the encryption key", failure.ErrorMessage);
    }

    [Fact]
    public void ResolveAlbumKey_WhenEncryptedAndUserHasKey_ReturnsSuccessResult()
    {
        var album = new Album
        {
            Id = "alb_resolve_test",
            IsEncrypted = true,
            EncryptedAlbumKeys = new Dictionary<string, string> { { "emp_1", "enc_album_key" } }
        };
        _albums.Save(album);

        _mockCrypto.Setup(c => c.DecryptRsa("enc_album_key", _privateKeyXml))
            .Returns("decrypted_album_aes");

        var user = new Employee { Id = "emp_1" };
        var result = _service.ResolveAlbumKey("alb_resolve_test", user);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsEncrypted);
        Assert.Equal("decrypted_album_aes", result.EncryptionKey);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ResolveChannelKey_WhenVaultLocked_ReturnsFailureResult()
    {
        var lockedKeystore = new ScopedKeystoreService(new EphemeralDataProtectionProvider());
        var lockedService = CreateService(lockedKeystore);

        var channel = new ChatChannel { Id = "chan_locked_test", IsEncrypted = true };
        _channels.Save(channel);
        var user = new Employee { Id = "emp_2" };

        var result = lockedService.ResolveChannelKey("chan_locked_test", user);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsEncrypted);
        Assert.Null(result.EncryptionKey);
        Assert.Equal("You do not have the encryption key for this channel.", result.ErrorMessage);
    }

    [Fact]
    public void CanAccess_And_CanUpload_UseStandardizedParameterOrder_And_DelegateToProvider()
    {
        var mockProvider = new Mock<IFileAccessProvider>();
        mockProvider.Setup(p => p.Category).Returns("custom_cat");
        mockProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx_1")).Returns(true);
        mockProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "ctx_1")).Returns(false);

        var serviceWithProvider = CreateService(providers: new[] { mockProvider.Object });

        var user = new Employee { Id = "emp_auth" };

        // Test standardized order (category, contextId, user)
        Assert.True(serviceWithProvider.CanAccess("custom_cat", "ctx_1", user));
        Assert.False(serviceWithProvider.CanUpload("custom_cat", "ctx_1", user));

        // Test fail-closed fallback for category with no provider (defaults to false)
        Assert.False(serviceWithProvider.CanAccess("unregistered_cat", "ctx_2", user));
        Assert.False(serviceWithProvider.CanUpload("unregistered_cat", "ctx_2", user));
    }

    [Fact]
    public void PluggableKeyResolvers_ResolveKeysPolymorphicallyWithoutConcreteRepositories()
    {
        var mockResolver = new Mock<IFileKeyResolver>();
        mockResolver.Setup(r => r.Category).Returns("custom_secure");
        mockResolver.Setup(r => r.ResolveUploadKey("ctx_up", It.IsAny<Employee>(), null, false, null))
            .Returns(FileKeyResolutionResult.Success("custom_upload_key"));
        mockResolver.Setup(r => r.ResolveDownloadKey("ctx_down", It.IsAny<Employee>(), null, null))
            .Returns(FileKeyResolutionResult.Success("custom_download_key"));

        var service = new FileAuthorizationService(
            providers: Enumerable.Empty<IFileAccessProvider>(),
            keyResolvers: new[] { mockResolver.Object });

        var user = new Employee { Id = "test_user" };

        var uploadResult = service.ResolveUploadKey("custom_secure", "ctx_up", user);
        Assert.True(uploadResult.IsSuccess);
        Assert.Equal("custom_upload_key", uploadResult.EncryptionKey);

        var downloadResult = service.ResolveDownloadKey("custom_secure", "ctx_down", user);
        Assert.True(downloadResult.IsSuccess);
        Assert.Equal("custom_download_key", downloadResult.EncryptionKey);

        // Unregistered category returns NotEncrypted
        var unencResult = service.ResolveDownloadKey("unknown_cat", "ctx", user);
        Assert.False(unencResult.IsEncrypted);

        // Test encapsulated FileUploadKeyRequest overload
        var request = new FileUploadKeyRequest("custom_secure", "ctx_up", user);
        var requestResult = service.ResolveUploadKey(request);
        Assert.True(requestResult.IsSuccess);
        Assert.Equal("custom_upload_key", requestResult.EncryptionKey);
    }
}
