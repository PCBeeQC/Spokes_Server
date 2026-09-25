using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Security;

public class ServerEscrowServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ICryptoService _crypto;

    public ServerEscrowServiceTests()
    {
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);

        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<ICryptoService, CryptoService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _crypto = _serviceProvider.GetRequiredService<ICryptoService>();
    }

    public override void Dispose()
    {
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Initialize_WhenNoMasterPassword_DoesNotInitializeEscrow()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        var profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = string.Empty;
        _db.CompanyProfile.Save(profile);

        var service = new ServerEscrowService(_db, _crypto);

        // Act
        service.Initialize();

        // Assert
        Assert.False(service.IsEscrowAvailable);
        Assert.Null(service.EscrowPublicKey);
        Assert.Null(service.DecryptedEscrowPrivateKey);
    }

    [Fact]
    public void Initialize_FirstTimeSetup_GeneratesNewEscrowKeyPairAndPersistsToConfig()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        var profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = "MasterPass123!";
        _db.CompanyProfile.Save(profile);

        var service = new ServerEscrowService(_db, _crypto);

        // Act
        service.Initialize();

        // Assert
        Assert.True(service.IsEscrowAvailable);
        Assert.False(string.IsNullOrEmpty(service.EscrowPublicKey));
        Assert.Contains("<RSAKeyValue>", service.EscrowPublicKey);
        Assert.False(string.IsNullOrEmpty(service.DecryptedEscrowPrivateKey));
        Assert.Contains("<RSAKeyValue>", service.DecryptedEscrowPrivateKey);

        var config = _db.ServerConfigs.GetOrCreateGlobalConfig();
        Assert.Equal(service.EscrowPublicKey, config.ServerMasterRsaPublicKey);
        Assert.False(string.IsNullOrEmpty(config.ServerMasterEncryptedPrivateKey));
        Assert.False(string.IsNullOrEmpty(config.ServerMasterKeySalt));

        // Verify that the generated key pair is functional for encryption/decryption
        var plainText = "TestEscrowPayload";
        var encrypted = _crypto.EncryptRsa(plainText, service.EscrowPublicKey);
        var decrypted = _crypto.DecryptRsa(encrypted, service.DecryptedEscrowPrivateKey);
        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void Initialize_SubsequentRun_LoadsAndDecryptsExistingEscrow()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        var profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = "MasterPass123!";
        _db.CompanyProfile.Save(profile);

        var firstService = new ServerEscrowService(_db, _crypto);
        firstService.Initialize();

        var originalPublicKey = firstService.EscrowPublicKey;
        var originalPrivateKey = firstService.DecryptedEscrowPrivateKey;

        // Act - instantiate new service with the same password configuration
        var secondService = new ServerEscrowService(_db, _crypto);
        secondService.Initialize();

        // Assert
        Assert.True(secondService.IsEscrowAvailable);
        Assert.Equal(originalPublicKey, secondService.EscrowPublicKey);
        Assert.Equal(originalPrivateKey, secondService.DecryptedEscrowPrivateKey);
    }

    [Fact]
    public void Initialize_WhenPasswordIncorrect_LeavesDecryptedPrivateKeyNull()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        var profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = "Pass1";
        _db.CompanyProfile.Save(profile);

        var firstService = new ServerEscrowService(_db, _crypto);
        firstService.Initialize();
        var originalPublicKey = firstService.EscrowPublicKey;
        Assert.True(firstService.IsEscrowAvailable);

        // Update profile with wrong password
        profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = "WrongPass";
        _db.CompanyProfile.Save(profile);

        var secondService = new ServerEscrowService(_db, _crypto);

        // Act
        secondService.Initialize();

        // Assert
        Assert.False(secondService.IsEscrowAvailable);
        Assert.Equal(originalPublicKey, secondService.EscrowPublicKey);
        Assert.Null(secondService.DecryptedEscrowPrivateKey);
    }

    [Fact]
    public void Initialize_MigratesFromJsonPasswordToEnvPassword_AndClearsJsonPassword()
    {
        // Arrange - initial setup with json password
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        var profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = "OldPassword";
        _db.CompanyProfile.Save(profile);

        var initialService = new ServerEscrowService(_db, _crypto);
        initialService.Initialize();
        var originalPublicKey = initialService.EscrowPublicKey;
        var originalPrivateKey = initialService.DecryptedEscrowPrivateKey;
        Assert.True(initialService.IsEscrowAvailable);

        // Set env variable to trigger migration
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", "NewEnvPassword");

        var migrationService = new ServerEscrowService(_db, _crypto);

        // Act
        migrationService.Initialize();

        // Assert
        // Verify profile.PublicChannelMasterPassword is wiped in database
        var reloadedProfile = _db.CompanyProfile.Get();
        Assert.Equal(string.Empty, reloadedProfile.PublicChannelMasterPassword);

        // Verify escrow was re-encrypted with NewEnvPassword and decrypted key is preserved
        Assert.True(migrationService.IsEscrowAvailable);
        Assert.Equal(originalPublicKey, migrationService.EscrowPublicKey);
        Assert.Equal(originalPrivateKey, migrationService.DecryptedEscrowPrivateKey);

        // Verify config in DB can be decrypted using the new KEK
        var config = _db.ServerConfigs.GetOrCreateGlobalConfig();
        Assert.False(string.IsNullOrEmpty(config.ServerMasterKeySalt));
        var newKek = _crypto.DeriveKeyFromPassword("NewEnvPassword", config.ServerMasterKeySalt, 100000);
        var decryptedWithNewKek = _crypto.DecryptAes(config.ServerMasterEncryptedPrivateKey, newKek);
        Assert.Equal(originalPrivateKey, decryptedWithNewKek);
    }

    [Fact]
    public void Initialize_WhenEnvPasswordSetAndJsonPasswordEmpty_InitializesEscrowSuccessfully()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", "EnvOnlySecret123!");
        var profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = string.Empty;
        _db.CompanyProfile.Save(profile);

        var service = new ServerEscrowService(_db, _crypto);

        // Act
        service.Initialize();

        // Assert
        Assert.True(service.IsEscrowAvailable);
        Assert.False(string.IsNullOrEmpty(service.EscrowPublicKey));
        Assert.False(string.IsNullOrEmpty(service.DecryptedEscrowPrivateKey));

        var config = _db.ServerConfigs.GetOrCreateGlobalConfig();
        Assert.Equal(service.EscrowPublicKey, config.ServerMasterRsaPublicKey);
        Assert.False(string.IsNullOrEmpty(config.ServerMasterEncryptedPrivateKey));
    }

    [Fact]
    public void Initialize_MigratesFromJsonPasswordToEnvPassword_WhenOldPasswordFailsToDecrypt_AbortsMigrationAndLeavesJsonPasswordIntact()
    {
        // Arrange - setup config encrypted with valid password
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        var profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = "ActualEncryptionPassword";
        _db.CompanyProfile.Save(profile);

        var initialService = new ServerEscrowService(_db, _crypto);
        initialService.Initialize();
        Assert.True(initialService.IsEscrowAvailable);

        // Now simulate a corrupt/mismatched JSON password in database
        profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = "MismatchPassword";
        _db.CompanyProfile.Save(profile);

        // Set env variable to trigger migration attempt
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", "NewEnvPassword");

        var failingService = new ServerEscrowService(_db, _crypto);

        // Act
        failingService.Initialize();

        // Assert
        // The migration should catch decryption failure and abort without wiping legacy password
        var reloadedProfile = _db.CompanyProfile.Get();
        Assert.Equal("MismatchPassword", reloadedProfile.PublicChannelMasterPassword);
        Assert.False(failingService.IsEscrowAvailable);
        Assert.Null(failingService.EscrowPublicKey);
        Assert.Null(failingService.DecryptedEscrowPrivateKey);
    }

    [Fact]
    public void Initialize_MigratesFromJsonPasswordToEnvPassword_WhenPasswordsMatch_ClearsJsonPasswordWithoutReEncrypting()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        var profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = "SamePassword123";
        _db.CompanyProfile.Save(profile);

        var initialService = new ServerEscrowService(_db, _crypto);
        initialService.Initialize();
        var originalPublicKey = initialService.EscrowPublicKey;
        var originalPrivateKey = initialService.DecryptedEscrowPrivateKey;

        // Set env variable to identical password
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", "SamePassword123");

        var migrationService = new ServerEscrowService(_db, _crypto);

        // Act
        migrationService.Initialize();

        // Assert
        var reloadedProfile = _db.CompanyProfile.Get();
        Assert.Equal(string.Empty, reloadedProfile.PublicChannelMasterPassword);
        Assert.True(migrationService.IsEscrowAvailable);
        Assert.Equal(originalPublicKey, migrationService.EscrowPublicKey);
        Assert.Equal(originalPrivateKey, migrationService.DecryptedEscrowPrivateKey);
    }

    [Fact]
    public void Initialize_AutoUpgradesLegacyStaticSalt_ToUniquePerInstanceSalt()
    {
        // Arrange - simulate existing deployment with legacy static salt and no ServerMasterKeySalt
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        var password = "LegacyPassword123!";
        var profile = _db.CompanyProfile.Get();
        profile.PublicChannelMasterPassword = password;
        _db.CompanyProfile.Save(profile);

        var (pub, priv) = _crypto.GenerateRsaKeyPair();
        var legacyKek = _crypto.DeriveKeyFromPassword(password, "ServerEscrowSalt_2024", 100000);
        var legacyEncryptedPriv = _crypto.EncryptAes(priv, legacyKek);

        var config = _db.ServerConfigs.GetOrCreateGlobalConfig();
        config.ServerMasterRsaPublicKey = pub;
        config.ServerMasterEncryptedPrivateKey = legacyEncryptedPriv;
        config.ServerMasterKeySalt = string.Empty; // Legacy deployment had no salt field
        _db.ServerConfigs.Save(config);

        // Act - run service initialization
        var service = new ServerEscrowService(_db, _crypto);
        service.Initialize();

        // Assert
        Assert.True(service.IsEscrowAvailable);
        Assert.Equal(pub, service.EscrowPublicKey);
        Assert.Equal(priv, service.DecryptedEscrowPrivateKey);

        // Verify that config was upgraded with a unique salt and re-encrypted
        var updatedConfig = _db.ServerConfigs.GetOrCreateGlobalConfig();
        Assert.False(string.IsNullOrEmpty(updatedConfig.ServerMasterKeySalt));
        Assert.NotEqual("ServerEscrowSalt_2024", updatedConfig.ServerMasterKeySalt);

        // Verify the newly encrypted private key decrypts with the new salt
        var upgradedKek = _crypto.DeriveKeyFromPassword(password, updatedConfig.ServerMasterKeySalt, 100000);
        var decryptedWithUpgradedKek = _crypto.DecryptAes(updatedConfig.ServerMasterEncryptedPrivateKey, upgradedKek);
        Assert.Equal(priv, decryptedWithUpgradedKek);

        // Verify that a subsequent boot continues to load seamlessly with the upgraded salt
        var subsequentService = new ServerEscrowService(_db, _crypto);
        subsequentService.Initialize();
        Assert.True(subsequentService.IsEscrowAvailable);
        Assert.Equal(pub, subsequentService.EscrowPublicKey);
        Assert.Equal(priv, subsequentService.DecryptedEscrowPrivateKey);
    }
}
