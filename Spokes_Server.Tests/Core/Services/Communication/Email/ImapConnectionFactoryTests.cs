using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Email;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class ImapConnectionFactoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly EmailAccountRepository _accounts;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly EncryptionService _encryptionService;
    private readonly ImapConnectionFactory _factory;

    public ImapConnectionFactoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ImapFactory_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _accounts = new EmailAccountRepository(_persistence, _config);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _encryptionService = new EncryptionService(_config);

        _factory = new ImapConnectionFactory(
            _companyProfiles,
            _encryptionService,
            _accounts,
            new Mock<ILogger<ImapConnectionFactory>>().Object);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ConnectAsync_WhenAccountNotFound_ReturnsMissingCredentials()
    {
        var result = await _factory.ConnectAsync("non_existent_id");

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.MissingCredentials, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task ConnectAsync_WhenAccountHasNoEmail_ReturnsMissingCredentials()
    {
        var account = new EmailAccount
        {
            Id = "acc1",
            Name = "John Doe",
            EmailAddress = "",
            EncryptedPassword = _encryptionService.Encrypt("pass123")
        };
        _accounts.Save(account);

        var result = await _factory.ConnectAsync(account.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.MissingCredentials, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task ConnectAsync_WhenAccountHasNoPassword_ReturnsMissingCredentials()
    {
        var account = new EmailAccount
        {
            Id = "acc2",
            Name = "John Doe",
            EmailAddress = "john@example.com",
            EncryptedPassword = ""
        };
        _accounts.Save(account);

        var result = await _factory.ConnectAsync(account);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.MissingCredentials, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task ConnectAsync_WhenServerNotConfigured_ReturnsServerNotConfigured()
    {
        var account = new EmailAccount
        {
            Id = "acc3",
            Name = "John Doe",
            EmailAddress = "john@example.com",
            EncryptedPassword = _encryptionService.Encrypt("secret")
        };
        _accounts.Save(account);

        var profile = _companyProfiles.Get();
        profile.EmailSettings.IncomingServers.Clear();
        _companyProfiles.Save(profile);

        var result = await _factory.ConnectAsync(account.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.ServerNotConfigured, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task ConnectAsync_WhenDecryptionFails_ReturnsDecryptionError()
    {
        var account = new EmailAccount
        {
            Id = "acc4",
            Name = "John Doe",
            EmailAddress = "john@example.com",
            CustomImapHost = "imap.example.com",
            EncryptedPassword = "not_valid_ciphertext_and_will_fail_mac"
        };
        _accounts.Save(account);

        var result = await _factory.ConnectAsync(account.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.DecryptionError, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task CreateAuthenticatedClientAsync_WhenMissingCredentials_ReturnsNull()
    {
        var client = await _factory.CreateAuthenticatedClientAsync("missing_acc");
        Assert.Null(client);
    }

    [Fact]
    public void ImapConnectionResult_Dispose_SafelyHandlesNullClient()
    {
        using var result = new ImapConnectionResult(null, ImapConnectionStatus.ServerNotConfigured);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ConnectAsync_WhenConnectionFails_ReturnsConnectionFailed()
    {
        var account = new EmailAccount
        {
            Id = "acc_net_fail",
            Name = "John Doe",
            EmailAddress = "john@example.com",
            CustomImapHost = "unresolvable-domain.invalid",
            CustomImapPort = 143,
            CustomImapSsl = false,
            EncryptedPassword = _encryptionService.Encrypt("secret")
        };
        _accounts.Save(account);

        var result = await _factory.ConnectAsync(account.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.ConnectionFailed, result.Status);
        Assert.Null(result.Client);
        Assert.NotNull(result.ErrorMessage);
    }
}
