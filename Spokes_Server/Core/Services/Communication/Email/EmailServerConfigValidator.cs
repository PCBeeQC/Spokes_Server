using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Localization;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public enum EmailServerValidationError
{
    None = 0,
    SslRequired,
    UnsafeExternalHost,
    MissingSharedCredential
}

/// <summary>
/// Single source of truth for authorizing, validating transport security (SSL + SSRF),
/// normalizing domains/labels, applying outgoing presets, and encrypting shared SMTP relay
/// credentials across EmailIncomingServerDialog and EmailOutgoingServerDialog.
/// </summary>
public static class EmailServerConfigValidator
{
    public const int DefaultImapPort = 993;
    public const int DefaultSmtpPort = 587;

    public static (bool IsAllowed, bool IsAdmin) AuthorizeServerManagement(Employee? actor, EmailServerSettings? settings)
    {
        if (actor == null || !actor.IsActive || actor.IsBanned || actor.IsSuspended)
        {
            return (false, false);
        }

        bool isAdmin = actor.IsAdmin || actor.HasPermission(AppPermissions.Admin.ManageSettings);
        bool canManage = isAdmin || (settings?.AllowUsersToManageServers == true);
        return (canManage, isAdmin);
    }

    public static string NormalizeDomain(string? domain) =>
        domain?.Trim().TrimStart('@').ToLowerInvariant() ?? string.Empty;

    public static async Task<EmailServerValidationError> ValidateTransportSecurityAsync(
        EmailServerConfigBase server,
        bool isAdmin,
        Func<string, Task>? externalHostValidator = null)
    {
        if (isAdmin)
        {
            return EmailServerValidationError.None;
        }

        if (!server.UseSsl)
        {
            return EmailServerValidationError.SslRequired;
        }

        try
        {
            var validator = externalHostValidator ?? (host => SafeExternalHttpHandler.AssertSafeExternalHostAsync(host));
            await validator(server.Host);
            return EmailServerValidationError.None;
        }
        catch
        {
            return EmailServerValidationError.UnsafeExternalHost;
        }
    }

    public static void NormalizeAndApplyDefaults(EmailServerConfigBase server, string? fallbackDomain = null)
    {
        var normalized = NormalizeDomain(server.Domain);
        server.Domain = !string.IsNullOrWhiteSpace(normalized)
            ? normalized
            : NormalizeDomain(fallbackDomain);

        if (string.IsNullOrWhiteSpace(server.Name))
        {
            server.Name = !string.IsNullOrWhiteSpace(server.Domain) ? server.Domain : server.Host;
        }
    }

    public static EmailServerValidationError ApplySharedCredentials(
        SendingServerConfig server,
        string? plainSharedPassword,
        EmailProviderPreset? preset,
        EncryptionService encryptionService)
    {
        if (!string.IsNullOrWhiteSpace(plainSharedPassword))
        {
            var trimmedPassword = plainSharedPassword.Trim();
            if (preset?.MirrorPasswordToUsername == true)
            {
                server.SharedUsername = trimmedPassword;
            }
            server.EncryptedSharedPassword = encryptionService.Encrypt(trimmedPassword);
        }

        if (server.UseSharedCredentials && string.IsNullOrEmpty(server.EncryptedSharedPassword))
        {
            return EmailServerValidationError.MissingSharedCredential;
        }

        return EmailServerValidationError.None;
    }

    public static string FormatValidationError(EmailServerValidationError error, IStringLocalizer Loc, bool isInlineOutgoing = false) => error switch
    {
        EmailServerValidationError.SslRequired => Loc["SSL / TLS is required for custom mail servers."].Value,
        EmailServerValidationError.UnsafeExternalHost => MailTransportSecurityHelper.SanitizedConnectionErrorMessage,
        EmailServerValidationError.MissingSharedCredential => isInlineOutgoing
            ? Loc["API Key / SMTP password is required for shared relay credentials."].Value
            : Loc["API Key / SMTP password is required when shared credentials are enabled."].Value,
        _ => string.Empty
    };

    public static string ApplyOutgoingPreset(
        SendingServerConfig server,
        string? previousPresetId,
        string? nextPresetId,
        bool isInline,
        string? fallbackDomain = null)
    {
        var previousPreset = EmailProviderPresets.GetById(previousPresetId);
        var resolvedPresetId = nextPresetId ?? EmailProviderPresets.CustomPresetId;
        var preset = EmailProviderPresets.GetById(resolvedPresetId);

        if (preset != null)
        {
            server.Name = isInline && preset.Scope == EmailPresetScope.MailboxProvider
                ? $"{preset.Name} SMTP"
                : preset.Name;
            server.Host = preset.SmtpHost;
            server.Port = preset.SmtpPort;
            server.UseSsl = preset.SmtpSsl;
            server.UseSharedCredentials = preset.UseSharedCredentials;
            server.SharedUsername = preset.DefaultSharedUsername;

            if (isInline && !string.IsNullOrWhiteSpace(fallbackDomain))
            {
                server.Domain = fallbackDomain;
            }
            else if (string.IsNullOrWhiteSpace(server.Domain) ||
                     (previousPreset != null && string.Equals(server.Domain, previousPreset.DefaultDomain, StringComparison.OrdinalIgnoreCase)))
            {
                server.Domain = preset.DefaultDomain;
            }
        }
        else if (previousPreset != null)
        {
            var previousExpectedName = isInline && previousPreset.Scope == EmailPresetScope.MailboxProvider
                ? $"{previousPreset.Name} SMTP"
                : previousPreset.Name;

            if (string.Equals(server.Name, previousExpectedName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(server.Name, previousPreset.Name, StringComparison.OrdinalIgnoreCase))
            {
                server.Name = string.Empty;
            }
            if (string.Equals(server.Host, previousPreset.SmtpHost, StringComparison.OrdinalIgnoreCase))
            {
                server.Host = string.Empty;
            }
            if (string.Equals(server.Domain, previousPreset.DefaultDomain, StringComparison.OrdinalIgnoreCase))
            {
                server.Domain = isInline && !string.IsNullOrWhiteSpace(fallbackDomain)
                    ? fallbackDomain
                    : string.Empty;
            }
        }

        return resolvedPresetId;
    }
}
