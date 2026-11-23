using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Models.Communication;

public enum EmailIdentitySourceType
{
    OutgoingServer = 0,
    RealInbox = 1,
    VirtualInbox = 2
}

/// <summary>
/// Represents a sharable outbound Sending Identity (e.g. "support@spokes.sh" via an Outgoing Server).
/// Can be auto-created from a Real Inbox, created from a Virtual Inbox, or created standalone from an Outgoing Server.
/// </summary>
public class EmailSendingIdentity : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is EmailSendingIdentity other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    public string OwnerEmployeeId { get; set; } = string.Empty;

    /// <summary>
    /// True when this Sending Identity was provisioned in Admin Server Settings (CompanySettings)
    /// or auto-created from a server-managed inbox.
    /// </summary>
    public bool IsServerManaged { get; set; }

    /// <summary>
    /// Tracks how this Sending Identity was created (from an Outgoing Server, a Real Inbox, or a Virtual Inbox).
    /// </summary>
    public EmailIdentitySourceType SourceType { get; set; } = EmailIdentitySourceType.OutgoingServer;

    /// <summary>
    /// Optional ID of the Real Inbox (EmailAccount) that auto-created or backs this identity.
    /// When the Outgoing Server uses per-user credentials and this identity has no explicit SMTP password,
    /// EmailDeliveryService inherits the credentials of this Real Inbox.
    /// </summary>
    public string? SourceAccountId { get; set; }

    /// <summary>
    /// Optional ID of the Virtual Inbox (EmailInbox) from which this identity was created.
    /// </summary>
    public string? SourceInboxId { get; set; }

    /// <summary>
    /// ID of the SendingServerConfig in EmailServerSettings.
    /// </summary>
    public string SendingServerConfigId { get; set; } = string.Empty;

    /// <summary>
    /// Friendly sender name shown in the From header (e.g. "Mats — Spokes Support").
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Local username part (e.g. "support") or full address if custom.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Domain part (e.g. "spokes.sh").
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    [JsonIgnore]
    public string EmailAddress
    {
        get
        {
            var cleanUser = (Username ?? string.Empty).Trim();
            if (cleanUser.Contains('@'))
                return cleanUser.ToLowerInvariant();

            var cleanDomain = (Domain ?? string.Empty).Trim().TrimStart('@');
            if (string.IsNullOrEmpty(cleanDomain))
                return cleanUser.ToLowerInvariant();

            return $"{cleanUser}@{cleanDomain}".ToLowerInvariant();
        }
    }

    [JsonIgnore]
    public string FormattedFrom =>
        string.IsNullOrWhiteSpace(DisplayName)
            ? EmailAddress
            : $"{DisplayName} <{EmailAddress}>";

    [JsonIgnore]
    public string EffectiveDomain
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Domain))
                return Domain.Trim().TrimStart('@').ToLowerInvariant();
            var addr = EmailAddress;
            var atIdx = addr.IndexOf('@');
            return atIdx >= 0 && atIdx < addr.Length - 1 ? addr[(atIdx + 1)..].ToLowerInvariant() : string.Empty;
        }
    }

    /// <summary>
    /// Rich HTML signature automatically appended when composing or replying from this identity.
    /// </summary>
    public string SignatureHtml { get; set; } = string.Empty;

    /// <summary>
    /// Optional per-identity SMTP credentials (used when the SendingServerConfig does not use shared credentials,
    /// or when using a custom SMTP server).
    /// </summary>
    public string CustomSmtpHost { get; set; } = string.Empty;
    public int CustomSmtpPort { get; set; } = 587;
    public bool CustomSmtpSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string EncryptedSmtpPassword { get; set; } = string.Empty;

    /// <summary>
    /// Individual Employee IDs granted permission to send from this identity.
    /// </summary>
    public List<string> AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Team IDs granted permission to send from this identity.
    /// </summary>
    public List<string> AllowedTeamIds { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool CanUserAccess(string employeeId, IEnumerable<string>? userTeamIds = null) =>
        EmailAccessHelper.CanUserAccess(IsServerManaged, OwnerEmployeeId, AllowedUserIds, AllowedTeamIds, employeeId, userTeamIds);
}
