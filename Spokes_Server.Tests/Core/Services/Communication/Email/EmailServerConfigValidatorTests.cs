using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class EmailServerConfigValidatorTests
{
    [Fact]
    public void AuthorizeServerManagement_EnforcesActiveStateAndSetting()
    {
        var admin = new Employee { Id = "admin", IsActive = true, IsAdmin = true };
        var regular = new Employee { Id = "user", IsActive = true };
        var suspendedAdmin = new Employee { Id = "sus", IsActive = true, IsAdmin = true, IsSuspended = true };

        var restrictedSettings = new EmailServerSettings { AllowUsersToManageServers = false };
        var openSettings = new EmailServerSettings { AllowUsersToManageServers = true };

        Assert.Equal((true, true), EmailServerConfigValidator.AuthorizeServerManagement(admin, restrictedSettings));
        Assert.Equal((false, false), EmailServerConfigValidator.AuthorizeServerManagement(regular, restrictedSettings));
        Assert.Equal((true, false), EmailServerConfigValidator.AuthorizeServerManagement(regular, openSettings));
        Assert.Equal((false, false), EmailServerConfigValidator.AuthorizeServerManagement(suspendedAdmin, openSettings));
        Assert.Equal((false, false), EmailServerConfigValidator.AuthorizeServerManagement(null, openSettings));
    }

    [Fact]
    public async Task ValidateTransportSecurityAsync_EnforcesSslAndExternalHostForNonAdmins()
    {
        var insecureServer = new SendingServerConfig { Host = "smtp.example.com", UseSsl = false };
        var loopbackServer = new SendingServerConfig { Host = "127.0.0.1", UseSsl = true };
        var validServer = new SendingServerConfig { Host = "smtp.example.com", UseSsl = true };

        Assert.Equal(EmailServerValidationError.SslRequired,
            await EmailServerConfigValidator.ValidateTransportSecurityAsync(insecureServer, isAdmin: false, _ => Task.CompletedTask));

        Assert.Equal(EmailServerValidationError.None,
            await EmailServerConfigValidator.ValidateTransportSecurityAsync(insecureServer, isAdmin: true, _ => throw new InvalidOperationException("blocked")));

        Assert.Equal(EmailServerValidationError.UnsafeExternalHost,
            await EmailServerConfigValidator.ValidateTransportSecurityAsync(loopbackServer, isAdmin: false, _ => throw new InvalidOperationException("ssrf")));

        Assert.Equal(EmailServerValidationError.None,
            await EmailServerConfigValidator.ValidateTransportSecurityAsync(validServer, isAdmin: false, _ => Task.CompletedTask));
    }

    [Fact]
    public void NormalizeAndApplyDefaults_CleansDomainAndPopulatesMissingName()
    {
        var server = new SendingServerConfig { Domain = " @Example.COM ", Host = "smtp.example.com", Name = "  " };
        EmailServerConfigValidator.NormalizeAndApplyDefaults(server);

        Assert.Equal("example.com", server.Domain);
        Assert.Equal("example.com", server.Name);

        var fallbackServer = new SendingServerConfig { Domain = "", Host = "smtp.fallback.test", Name = "" };
        EmailServerConfigValidator.NormalizeAndApplyDefaults(fallbackServer, fallbackDomain: "@Fallback.Test");

        Assert.Equal("fallback.test", fallbackServer.Domain);
        Assert.Equal("fallback.test", fallbackServer.Name);
    }

    [Fact]
    public void ApplyOutgoingPreset_InlineSwitchingBackToCustom_ClearsPreviousPresetFields()
    {
        var server = new SendingServerConfig();

        var gmailId = EmailServerConfigValidator.ApplyOutgoingPreset(
            server,
            previousPresetId: EmailProviderPresets.CustomPresetId,
            nextPresetId: "gmail",
            isInline: true,
            fallbackDomain: null);

        Assert.Equal("gmail", gmailId);
        Assert.Equal("Google Workspace / Gmail SMTP", server.Name);
        Assert.Equal("smtp.gmail.com", server.Host);
        Assert.Equal("gmail.com", server.Domain);

        var customId = EmailServerConfigValidator.ApplyOutgoingPreset(
            server,
            previousPresetId: gmailId,
            nextPresetId: EmailProviderPresets.CustomPresetId,
            isInline: true,
            fallbackDomain: null);

        Assert.Equal(EmailProviderPresets.CustomPresetId, customId);
        Assert.Equal(string.Empty, server.Name);
        Assert.Equal(string.Empty, server.Host);
        Assert.Equal(string.Empty, server.Domain);
    }

    [Fact]
    public void ApplySharedCredentials_EncryptsAndMirrorsPasswordWhenPresetRequiresIt()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SPOKES_MASTER_PASSWORD"] = "test-master-password-1234567890!"
            })
            .Build();
        var encryption = new EncryptionService(config);
        var server = new SendingServerConfig { UseSharedCredentials = true };
        var mirrorPreset = new EmailProviderPreset(
            Id: "mirror",
            Name: "Mirror Relay",
            DefaultDomain: "example.com",
            Scope: EmailPresetScope.OutgoingRelay,
            ImapHost: "",
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.mirror.test",
            SmtpPort: 587,
            SmtpSsl: true,
            UseSharedCredentials: true,
            DefaultSharedUsername: "",
            CredentialLabel: "API Key",
            CredentialPlaceholder: "",
            InstructionsText: "",
            MirrorPasswordToUsername: true);

        Assert.Equal(EmailServerValidationError.MissingSharedCredential,
            EmailServerConfigValidator.ApplySharedCredentials(server, "", mirrorPreset, encryption));

        Assert.Equal(EmailServerValidationError.None,
            EmailServerConfigValidator.ApplySharedCredentials(server, "  secret-token  ", mirrorPreset, encryption));

        Assert.Equal("secret-token", server.SharedUsername);
        Assert.Equal("secret-token", encryption.Decrypt(server.EncryptedSharedPassword));
    }
}
