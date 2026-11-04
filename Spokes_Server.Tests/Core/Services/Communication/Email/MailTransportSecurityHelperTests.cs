using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class MailTransportSecurityHelperTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;

    public MailTransportSecurityHelperTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_MailTransport_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
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

    [Theory]
    [InlineData(0, true, 993)]
    [InlineData(0, false, 143)]
    [InlineData(1993, true, 1993)]
    public void NormalizeImapPort_ResolvesExpectedDefaults(int inputPort, bool useSsl, int expectedPort)
    {
        Assert.Equal(expectedPort, MailTransportSecurityHelper.NormalizeImapPort(inputPort, useSsl));
    }

    [Theory]
    [InlineData(0, true, 465)]
    [InlineData(0, false, 587)]
    [InlineData(2525, true, 2525)]
    public void NormalizeSmtpPort_ResolvesExpectedDefaults(int inputPort, bool useSsl, int expectedPort)
    {
        Assert.Equal(expectedPort, MailTransportSecurityHelper.NormalizeSmtpPort(inputPort, useSsl));
    }

    [Theory]
    [InlineData(993, true, false, SecureSocketOptions.SslOnConnect)]
    [InlineData(0, true, false, SecureSocketOptions.SslOnConnect)]
    [InlineData(143, true, false, SecureSocketOptions.StartTls)]
    [InlineData(1993, true, false, SecureSocketOptions.StartTls)]
    [InlineData(143, false, true, SecureSocketOptions.None)]
    public void ResolveImapTlsOptions_EnforcesStrictTls_AndNeverUsesOpportunisticStartTls(
        int port,
        bool useSsl,
        bool allowPlaintext,
        SecureSocketOptions expected)
    {
        var resolved = MailTransportSecurityHelper.ResolveImapTlsOptions(port, useSsl, allowPlaintext);
        Assert.Equal(expected, resolved);
        Assert.NotEqual(SecureSocketOptions.StartTlsWhenAvailable, resolved);
    }

    [Fact]
    public void ResolveImapTlsOptions_WhenUseSslFalseAndPlaintextDisallowed_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MailTransportSecurityHelper.ResolveImapTlsOptions(143, useSsl: false, allowPlaintext: false));
    }

    [Theory]
    [InlineData(465, true, false, SecureSocketOptions.SslOnConnect)]
    [InlineData(0, true, false, SecureSocketOptions.SslOnConnect)]
    [InlineData(587, true, false, SecureSocketOptions.StartTls)]
    [InlineData(25, true, false, SecureSocketOptions.StartTls)]
    [InlineData(2525, true, false, SecureSocketOptions.StartTls)]
    [InlineData(25, false, true, SecureSocketOptions.None)]
    public void ResolveSmtpTlsOptions_EnforcesStrictTls_AndNeverUsesOpportunisticStartTls(
        int port,
        bool useSsl,
        bool allowPlaintext,
        SecureSocketOptions expected)
    {
        var resolved = MailTransportSecurityHelper.ResolveSmtpTlsOptions(port, useSsl, allowPlaintext);
        Assert.Equal(expected, resolved);
        Assert.NotEqual(SecureSocketOptions.StartTlsWhenAvailable, resolved);
    }

    [Fact]
    public void ResolveSmtpTlsOptions_WhenUseSslFalseAndPlaintextDisallowed_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MailTransportSecurityHelper.ResolveSmtpTlsOptions(587, useSsl: false, allowPlaintext: false));
    }

    [Fact]
    public void ResolveBillInboxTlsOptions_EnforcesStartTlsOrImplicitSslByDefault_AndAllowsPlaintextOnlyWhenExplicitlyEnabled()
    {
        var defaults = new CompanyBillInboxSettings();
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailTransportSecurityHelper.ResolveBillInboxImapTlsOptions(defaults));
        Assert.Equal(SecureSocketOptions.StartTls, MailTransportSecurityHelper.ResolveBillInboxSmtpTlsOptions(defaults));

        var implicitSmtp = new CompanyBillInboxSettings { SmtpPort = 465, SmtpSsl = false, AllowPlaintextTransport = false };
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailTransportSecurityHelper.ResolveBillInboxSmtpTlsOptions(implicitSmtp));

        var explicitImapStartTls = new CompanyBillInboxSettings { ImapPort = 143, ImapSsl = false, AllowPlaintextTransport = false };
        Assert.Equal(SecureSocketOptions.StartTls, MailTransportSecurityHelper.ResolveBillInboxImapTlsOptions(explicitImapStartTls));

        var explicitPlaintext = new CompanyBillInboxSettings
        {
            ImapPort = 143,
            ImapSsl = false,
            SmtpPort = 25,
            SmtpSsl = false,
            AllowPlaintextTransport = true
        };
        Assert.Equal(SecureSocketOptions.None, MailTransportSecurityHelper.ResolveBillInboxImapTlsOptions(explicitPlaintext));
        Assert.Equal(SecureSocketOptions.None, MailTransportSecurityHelper.ResolveBillInboxSmtpTlsOptions(explicitPlaintext));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    [InlineData("localhost")]
    public async Task ConnectMailServiceAsync_WhenRequireSafeExternalHostTrue_BlocksInternalAndMetadataTargets(string blockedHost)
    {
        using var imapClient = new ImapClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MailTransportSecurityHelper.ConnectMailServiceAsync(
                imapClient,
                blockedHost,
                993,
                SecureSocketOptions.SslOnConnect,
                requireSafeExternalHost: true));

        using var smtpClient = new SmtpClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MailTransportSecurityHelper.ConnectMailServiceAsync(
                smtpClient,
                blockedHost,
                587,
                SecureSocketOptions.StartTls,
                requireSafeExternalHost: true));
    }

    [Fact]
    public void FormatConnectionError_SanitizesInternalDetailsForNonAdmins_AndPreservesDetailsForAdmins()
    {
        var rawException = new InvalidOperationException("220 internal-exchange.corp.local ESMTP Postfix");

        var nonAdminMessage = MailTransportSecurityHelper.FormatConnectionError(rawException, callerIsAdmin: false);
        Assert.Equal(MailTransportSecurityHelper.SanitizedConnectionErrorMessage, nonAdminMessage);
        Assert.DoesNotContain("internal-exchange", nonAdminMessage, StringComparison.OrdinalIgnoreCase);

        var adminMessage = MailTransportSecurityHelper.FormatConnectionError(rawException, callerIsAdmin: true);
        Assert.Contains("internal-exchange.corp.local", adminMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImapConnectionFactory_TestConnectionAsync_BlocksNonAdminOnServerManaged_EnforcesSsrfOnBogusPresetFallback_AndSanitizesErrors()
    {
        var companyRepo = new CompanyProfileRepository(_persistence, _config);
        var accountsRepo = new EmailAccountRepository(_persistence, _config);
        var employeesRepo = new EmployeeRepository(_persistence, _config);
        var encryption = new EncryptionService(_config);

        var regularUser = new Employee { Id = "emp-user", FirstName = "Regular", IsActive = true, IsAdmin = false };
        var adminUser = new Employee { Id = "emp-admin", FirstName = "Admin", IsActive = true, IsAdmin = true };
        employeesRepo.Save(regularUser);
        employeesRepo.Save(adminUser);

        var factory = new ImapConnectionFactory(
            companyRepo,
            encryption,
            accountsRepo,
            new Mock<ILogger<ImapConnectionFactory>>().Object,
            employeesRepo);

        var serverManagedAccount = new EmailAccount
        {
            Id = "acc-srv",
            IsServerManaged = true,
            EmailAddress = "info@company.com",
            CustomImapHost = "127.0.0.1",
            CustomImapPort = 993,
            CustomImapSsl = true
        };

        var serverManagedAttempt = await factory.TestConnectionAsync(serverManagedAccount, "secret", regularUser);
        Assert.False(serverManagedAttempt.Success);
        Assert.Contains("Not authorized", serverManagedAttempt.Message, StringComparison.OrdinalIgnoreCase);

        // Personal account with a bogus IncomingServerConfigId falling back to CustomImapHost = 127.0.0.1:
        // must still be treated as untrusted (SSRF blocked) and sanitized for non-admin callers.
        var personalLoopbackAccount = new EmailAccount
        {
            Id = "acc-personal-ssrf",
            IsServerManaged = false,
            OwnerEmployeeId = regularUser.Id,
            IncomingServerConfigId = "non-existent-preset-id",
            EmailAddress = "user@external.com",
            CustomImapHost = "127.0.0.1",
            CustomImapPort = 993,
            CustomImapSsl = true
        };

        var nonAdminResult = await factory.TestConnectionAsync(personalLoopbackAccount, "secret", regularUser);
        Assert.False(nonAdminResult.Success);
        Assert.Equal(MailTransportSecurityHelper.SanitizedConnectionErrorMessage, nonAdminResult.Message);
        Assert.DoesNotContain("127.0.0.1", nonAdminResult.Message, StringComparison.Ordinal);

        var adminResult = await factory.TestConnectionAsync(personalLoopbackAccount, "secret", adminUser);
        Assert.False(adminResult.Success);
        Assert.NotEqual(MailTransportSecurityHelper.SanitizedConnectionErrorMessage, adminResult.Message);
    }
}
