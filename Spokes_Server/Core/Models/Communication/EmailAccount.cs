using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Models.Communication;

/// <summary>
/// Represents a Real Inbox (an existing physical mailbox on an IMAP server).
/// Messages and folders for this mailbox are stored once under Data/EmailAccounts/{Id}/
/// and can be shared across multiple Spokes users and teams without duplicating disk storage.
/// </summary>
public class EmailAccount : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is EmailAccount other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string OwnerEmployeeId { get; set; } = string.Empty;

    /// <summary>
    /// True when this Real Inbox was provisioned in Admin Server Settings (CompanySettings).
    /// False when connected by a user as a personal mailbox in User Email Settings.
    /// Admins cannot view or manage users' personal (IsServerManaged == false) mailboxes in Server Settings,
    /// and server-managed mailboxes only appear on a user's Email page when shared via AllowedUserIds or AllowedTeamIds.
    /// </summary>
    public bool IsServerManaged { get; set; }

    public string EmailAddress { get; set; } = string.Empty;

    /// <summary>
    /// Optional ID of an IncomingServerConfig preset in EmailServerSettings.
    /// When null or empty, CustomImapHost / CustomImapPort / CustomImapSsl are used.
    /// </summary>
    public string? IncomingServerConfigId { get; set; }

    public string CustomImapHost { get; set; } = string.Empty;
    public int CustomImapPort { get; set; } = 993;
    public bool CustomImapSsl { get; set; } = true;

    /// <summary>
    /// Optional ID of a SendingServerConfig preset in EmailServerSettings used for this Real Inbox's auto-created Sending Identity.
    /// Defaults to the IncomingServerConfig's DefaultOutgoingServerId when not explicitly overridden.
    /// </summary>
    public string? OutgoingServerConfigId { get; set; }

    public string CustomSmtpHost { get; set; } = string.Empty;
    public int CustomSmtpPort { get; set; } = 587;
    public bool CustomSmtpSsl { get; set; } = true;

    public string Username { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>
    /// ID of the Sending Identity automatically provisioned when this Real Inbox was created.
    /// </summary>
    public string? AutoCreatedIdentityId { get; set; }

    /// <summary>
    /// Additional Sending Identity IDs linked to this Real Inbox.
    /// </summary>
    public List<string> LinkedIdentityIds { get; set; } = [];

    /// <summary>
    /// Default Sending Identity ID when composing from this Real Inbox.
    /// </summary>
    public string? DefaultIdentityId { get; set; }

    /// <summary>
    /// When true, users replying in this Real Inbox may also select their own personal/shared Sending Identity
    /// whose domain matches the Real Inbox's domain.
    /// </summary>
    public bool AllowDomainMatchedPersonalIdentity { get; set; } = true;

    /// <summary>
    /// Individual Employee IDs granted access to monitor and use this Real Inbox.
    /// </summary>
    public List<string> AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Team IDs granted access to monitor and use this Real Inbox.
    /// </summary>
    public List<string> AllowedTeamIds { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string? PrimaryDomain
    {
        get
        {
            if (string.IsNullOrWhiteSpace(EmailAddress)) return null;
            var atIdx = EmailAddress.IndexOf('@');
            return atIdx >= 0 && atIdx < EmailAddress.Length - 1
                ? EmailAddress[(atIdx + 1)..].Trim().ToLowerInvariant()
                : null;
        }
    }

    public bool CanUserAccess(string employeeId, IEnumerable<string>? userTeamIds = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return false;
        if (!IsServerManaged && string.Equals(OwnerEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)) return true;
        if (AllowedUserIds != null && AllowedUserIds.Any(u => string.Equals(u, employeeId, StringComparison.OrdinalIgnoreCase))) return true;
        if (userTeamIds != null && AllowedTeamIds != null && AllowedTeamIds.Intersect(userTeamIds, StringComparer.OrdinalIgnoreCase).Any()) return true;
        return false;
    }
}
