using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Email;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class ImapConnectionFactoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly EncryptionService _encryptionService;
    private readonly ImapConnectionFactory _factory;

    public ImapConnectionFactoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ImapFactory_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _employees = new EmployeeRepository(_persistence, _config);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _encryptionService = new EncryptionService(_config);

        _factory = new ImapConnectionFactory(
            _companyProfiles,
            _employees,
            _encryptionService,
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
    public async Task ConnectAsync_WhenEmployeeNotFound_ReturnsMissingCredentials()
    {
        var result = await _factory.ConnectAsync("non_existent_id");

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.MissingCredentials, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task ConnectAsync_WhenEmployeeHasNoEmail_ReturnsMissingCredentials()
    {
        var emp = new Employee
        {
            Id = "emp1",
            FirstName = "John",
            LastName = "Doe",
            Email = "",
            EncryptedEmailPassword = _encryptionService.Encrypt("pass123")
        };
        _employees.Save(emp);

        var result = await _factory.ConnectAsync(emp.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.MissingCredentials, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task ConnectAsync_WhenEmployeeHasNoPassword_ReturnsMissingCredentials()
    {
        var emp = new Employee
        {
            Id = "emp2",
            FirstName = "John",
            LastName = "Doe",
            Email = "john@example.com",
            EncryptedEmailPassword = ""
        };
        _employees.Save(emp);

        var result = await _factory.ConnectAsync(emp);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.MissingCredentials, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task ConnectAsync_WhenServerNotConfigured_ReturnsServerNotConfigured()
    {
        var emp = new Employee
        {
            Id = "emp3",
            FirstName = "John",
            LastName = "Doe",
            Email = "john@example.com",
            EncryptedEmailPassword = _encryptionService.Encrypt("secret")
        };
        _employees.Save(emp);

        var profile = _companyProfiles.Get();
        profile.EmailSettings.ImapHost = "";
        _companyProfiles.Save(profile);

        var result = await _factory.ConnectAsync(emp.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.ServerNotConfigured, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task ConnectAsync_WhenDecryptionFails_ReturnsDecryptionError()
    {
        var emp = new Employee
        {
            Id = "emp4",
            FirstName = "John",
            LastName = "Doe",
            Email = "john@example.com",
            EncryptedEmailPassword = "not_valid_ciphertext_and_will_fail_mac"
        };
        _employees.Save(emp);

        var profile = _companyProfiles.Get();
        profile.EmailSettings.ImapHost = "imap.example.com";
        _companyProfiles.Save(profile);

        var result = await _factory.ConnectAsync(emp.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.DecryptionError, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task CreateAuthenticatedClientAsync_WhenMissingCredentials_ReturnsNull()
    {
        var client = await _factory.CreateAuthenticatedClientAsync("missing_emp");
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
        var emp = new Employee
        {
            Id = "emp_net_fail",
            FirstName = "John",
            LastName = "Doe",
            Email = "john@example.com",
            EncryptedEmailPassword = _encryptionService.Encrypt("secret")
        };
        _employees.Save(emp);

        var profile = _companyProfiles.Get();
        profile.EmailSettings.ImapHost = "unresolvable-domain.invalid";
        profile.EmailSettings.ImapPort = 143;
        profile.EmailSettings.ImapSsl = false;
        _companyProfiles.Save(profile);

        var result = await _factory.ConnectAsync(emp.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.ConnectionFailed, result.Status);
        Assert.Null(result.Client);
        Assert.NotNull(result.ErrorMessage);
    }
}
