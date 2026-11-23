using System;
using System.Collections.Generic;
using System.Linq;

namespace Spokes_Server.Core.Configuration;

public enum EmailPresetScope
{
    /// <summary>
    /// Full mailbox provider offering both Incoming (IMAP) and Outgoing (SMTP) servers.
    /// </summary>
    MailboxProvider = 0,

    /// <summary>
    /// Dedicated Outgoing SMTP relay service (e.g. Resend, Postmark, SendGrid, Mailgun, Brevo).
    /// </summary>
    OutgoingRelay = 1
}

/// <summary>
/// Declarative definition for an external email or SMTP relay provider preset.
/// To add a new provider preset in the future, add an entry to <see cref="EmailProviderPresets.All"/>.
/// </summary>
public sealed record EmailProviderPreset(
    string Id,
    string Name,
    string DefaultDomain,
    EmailPresetScope Scope,
    string ImapHost,
    int ImapPort,
    bool ImapSsl,
    string SmtpHost,
    int SmtpPort,
    bool SmtpSsl,
    bool UseSharedCredentials,
    string DefaultSharedUsername,
    string CredentialLabel,
    string CredentialPlaceholder,
    string InstructionsText,
    string? HelpUrl = null,
    string? HelpLinkLabel = null,
    bool RequiresCustomSmtpUsername = false,
    bool MirrorPasswordToUsername = false);

public static class EmailProviderPresets
{
    public const string CustomPresetId = "custom";

    public static readonly IReadOnlyList<EmailProviderPreset> All =
    [
        // ==================== MAILBOX PROVIDERS (IMAP + SMTP) ====================
        new(
            Id: "gmail",
            Name: "Google Workspace / Gmail",
            DefaultDomain: "gmail.com",
            Scope: EmailPresetScope.MailboxProvider,
            ImapHost: "imap.gmail.com",
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.gmail.com",
            SmtpPort: 465,
            SmtpSsl: true,
            UseSharedCredentials: false,
            DefaultSharedUsername: string.Empty,
            CredentialLabel: "Google App Password (16 characters)",
            CredentialPlaceholder: "xxxx xxxx xxxx xxxx",
            InstructionsText: "Google requires 2-Step Verification and a 16-character Google App Password (your regular Google account password will not work for IMAP/SMTP).",
            HelpUrl: "https://myaccount.google.com/apppasswords",
            HelpLinkLabel: "Generate Google App Password ↗"),

        new(
            Id: "microsoft365",
            Name: "Microsoft 365 / Outlook",
            DefaultDomain: "outlook.com",
            Scope: EmailPresetScope.MailboxProvider,
            ImapHost: "outlook.office365.com",
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.office365.com",
            SmtpPort: 587,
            SmtpSsl: true,
            UseSharedCredentials: false,
            DefaultSharedUsername: string.Empty,
            CredentialLabel: "App Password / Mailbox Password",
            CredentialPlaceholder: "",
            InstructionsText: "Ensure IMAP and Authenticated SMTP are enabled for the mailbox. If Two-Step Verification is enabled on your Microsoft account, generate an App Password in Security Info.",
            HelpUrl: "https://mysignins.microsoft.com/security-info",
            HelpLinkLabel: "Open Microsoft Security Info ↗"),

        new(
            Id: "icloud",
            Name: "Apple iCloud Mail",
            DefaultDomain: "icloud.com",
            Scope: EmailPresetScope.MailboxProvider,
            ImapHost: "imap.mail.me.com",
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.mail.me.com",
            SmtpPort: 587,
            SmtpSsl: true,
            UseSharedCredentials: false,
            DefaultSharedUsername: string.Empty,
            CredentialLabel: "Apple App-Specific Password",
            CredentialPlaceholder: "xxxx-xxxx-xxxx-xxxx",
            InstructionsText: "Apple iCloud Mail requires an App-Specific Password generated from your Apple Account page under Sign-In and Security -> App-Specific Passwords.",
            HelpUrl: "https://account.apple.com/sign-in",
            HelpLinkLabel: "Generate Apple App-Specific Password ↗"),

        new(
            Id: "zoho",
            Name: "Zoho Mail",
            DefaultDomain: "zoho.com",
            Scope: EmailPresetScope.MailboxProvider,
            ImapHost: "imappro.zoho.com",
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtppro.zoho.com",
            SmtpPort: 465,
            SmtpSsl: true,
            UseSharedCredentials: false,
            DefaultSharedUsername: string.Empty,
            CredentialLabel: "Zoho App-Specific Password",
            CredentialPlaceholder: "",
            InstructionsText: "Enable IMAP Access in Zoho Mail Settings -> Mail Accounts -> IMAP. If Two-Factor Authentication is enabled, generate an App-Specific Password under Zoho My Account -> Security -> App Passwords.",
            HelpUrl: "https://accounts.zoho.com/home#security/device_logins",
            HelpLinkLabel: "Open Zoho App Passwords ↗"),

        new(
            Id: "fastmail",
            Name: "Fastmail",
            DefaultDomain: "fastmail.com",
            Scope: EmailPresetScope.MailboxProvider,
            ImapHost: "imap.fastmail.com",
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.fastmail.com",
            SmtpPort: 465,
            SmtpSsl: true,
            UseSharedCredentials: false,
            DefaultSharedUsername: string.Empty,
            CredentialLabel: "Fastmail App Password",
            CredentialPlaceholder: "",
            InstructionsText: "Fastmail requires a dedicated App Password with IMAP & SMTP access (Settings -> Privacy & Security -> Integrations -> New App Password).",
            HelpUrl: "https://app.fastmail.com/settings/security/tokens",
            HelpLinkLabel: "Create Fastmail App Password ↗"),

        new(
            Id: "yahoo",
            Name: "Yahoo Mail",
            DefaultDomain: "yahoo.com",
            Scope: EmailPresetScope.MailboxProvider,
            ImapHost: "imap.mail.yahoo.com",
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.mail.yahoo.com",
            SmtpPort: 465,
            SmtpSsl: true,
            UseSharedCredentials: false,
            DefaultSharedUsername: string.Empty,
            CredentialLabel: "Yahoo App Password",
            CredentialPlaceholder: "",
            InstructionsText: "Yahoo Mail requires generating an App Password under Account Info -> Account Security -> Generate app password.",
            HelpUrl: "https://login.yahoo.com/account/security",
            HelpLinkLabel: "Open Yahoo Account Security ↗"),

        // ==================== OUTGOING RELAY PROVIDERS (SMTP) ====================
        new(
            Id: "resend",
            Name: "Resend",
            DefaultDomain: string.Empty,
            Scope: EmailPresetScope.OutgoingRelay,
            ImapHost: string.Empty,
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.resend.com",
            SmtpPort: 465,
            SmtpSsl: true,
            UseSharedCredentials: true,
            DefaultSharedUsername: "resend",
            CredentialLabel: "Resend API Key",
            CredentialPlaceholder: "re_123456789...",
            InstructionsText: "Verify your sending domain in Resend, then create an API Key with Sending Access. Spokes automatically configures smtp.resend.com (port 465) and username 'resend'.",
            HelpUrl: "https://resend.com/api-keys",
            HelpLinkLabel: "Get Resend API Key ↗"),

        new(
            Id: "postmark",
            Name: "Postmark",
            DefaultDomain: string.Empty,
            Scope: EmailPresetScope.OutgoingRelay,
            ImapHost: string.Empty,
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.postmarkapp.com",
            SmtpPort: 587,
            SmtpSsl: true,
            UseSharedCredentials: true,
            DefaultSharedUsername: string.Empty,
            CredentialLabel: "Postmark Server API Token",
            CredentialPlaceholder: "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
            InstructionsText: "In Postmark, open your Server -> API Tokens tab and copy the Server API Token. Spokes automatically uses this token as both the SMTP username and password on smtp.postmarkapp.com.",
            HelpUrl: "https://account.postmarkapp.com/servers",
            HelpLinkLabel: "Open Postmark Servers ↗",
            MirrorPasswordToUsername: true),

        new(
            Id: "sendgrid",
            Name: "Twilio SendGrid",
            DefaultDomain: string.Empty,
            Scope: EmailPresetScope.OutgoingRelay,
            ImapHost: string.Empty,
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.sendgrid.net",
            SmtpPort: 465,
            SmtpSsl: true,
            UseSharedCredentials: true,
            DefaultSharedUsername: "apikey",
            CredentialLabel: "SendGrid API Key",
            CredentialPlaceholder: "SG.xxxx...",
            InstructionsText: "Create an API Key with Mail Send permissions in SendGrid. Spokes automatically configures smtp.sendgrid.net (port 465) and username 'apikey'.",
            HelpUrl: "https://app.sendgrid.com/settings/api_keys",
            HelpLinkLabel: "Create SendGrid API Key ↗"),

        new(
            Id: "mailgun",
            Name: "Mailgun",
            DefaultDomain: string.Empty,
            Scope: EmailPresetScope.OutgoingRelay,
            ImapHost: string.Empty,
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp.mailgun.org",
            SmtpPort: 465,
            SmtpSsl: true,
            UseSharedCredentials: true,
            DefaultSharedUsername: string.Empty,
            CredentialLabel: "Mailgun SMTP Password",
            CredentialPlaceholder: "",
            InstructionsText: "In Mailgun, go to Sending -> Domain Settings -> SMTP Credentials to create an SMTP login (e.g. postmaster@yourdomain.com) and password.",
            HelpUrl: "https://app.mailgun.com/mg/sending/domains",
            HelpLinkLabel: "Open Mailgun Domains ↗",
            RequiresCustomSmtpUsername: true),

        new(
            Id: "brevo",
            Name: "Brevo (Sendinblue)",
            DefaultDomain: string.Empty,
            Scope: EmailPresetScope.OutgoingRelay,
            ImapHost: string.Empty,
            ImapPort: 993,
            ImapSsl: true,
            SmtpHost: "smtp-relay.brevo.com",
            SmtpPort: 587,
            SmtpSsl: true,
            UseSharedCredentials: true,
            DefaultSharedUsername: string.Empty,
            CredentialLabel: "Brevo SMTP Key",
            CredentialPlaceholder: "xkeysib-...",
            InstructionsText: "In Brevo, open SMTP & API -> SMTP to view your SMTP Login email and generate an SMTP Key.",
            HelpUrl: "https://app.brevo.com/settings/keys/smtp",
            HelpLinkLabel: "Open Brevo SMTP Keys ↗",
            RequiresCustomSmtpUsername: true)
    ];

    public static IReadOnlyList<EmailProviderPreset> MailboxProviders =>
        All.Where(p => p.Scope == EmailPresetScope.MailboxProvider).ToList();

    public static IReadOnlyList<EmailProviderPreset> OutgoingProviders =>
        All.Where(p => p.Scope == EmailPresetScope.OutgoingRelay).Concat(MailboxProviders).ToList();

    public static EmailProviderPreset? GetById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static EmailProviderPreset? DetectByImapHost(string? imapHost)
    {
        if (string.IsNullOrWhiteSpace(imapHost)) return null;
        return MailboxProviders.FirstOrDefault(p =>
            string.Equals(p.ImapHost, imapHost.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> PublicMailboxDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com",
        "outlook.com", "hotmail.com", "live.com", "msn.com",
        "icloud.com", "me.com", "mac.com",
        "yahoo.com", "ymail.com",
        "zoho.com", "fastmail.com",
        "proton.me", "protonmail.com", "pm.me",
        "aol.com", "gmx.com", "gmx.net", "mail.com"
    };

    private static readonly string[] PublicMailboxProviderPrefixes =
    [
        "gmail.", "googlemail.", "outlook.", "hotmail.", "live.", "yahoo.", "icloud.", "protonmail.", "fastmail."
    ];

    public static EmailProviderPreset? DetectBySmtpHost(string? smtpHost)
    {
        if (string.IsNullOrWhiteSpace(smtpHost)) return null;
        return All.FirstOrDefault(p =>
            string.Equals(p.SmtpHost, smtpHost.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true if the domain belongs to a shared consumer/public mailbox provider (e.g. gmail.com, outlook.com)
    /// rather than an organization's own custom domain.
    /// </summary>
    public static bool IsPublicMailboxDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var normalized = domain.Trim().TrimStart('@').ToLowerInvariant();
        if (PublicMailboxDomains.Contains(normalized)) return true;
        return PublicMailboxProviderPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
