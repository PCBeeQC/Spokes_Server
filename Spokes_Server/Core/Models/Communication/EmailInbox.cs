using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Models.Communication;

/// <summary>
/// Represents a Virtual Inbox filtered by Spokes from an existing Real Inbox (EmailAccount)
/// based on explicit target email addresses or domain patterns (e.g. "support@spokes.sh" or "@spokes.sh").
/// Can be shared with individuals or teams and linked to Sending Identities.
/// </summary>
public class EmailInbox : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is EmailInbox other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string OwnerEmployeeId { get; set; } = string.Empty;

    /// <summary>
    /// True when this Virtual Inbox was provisioned in Admin Server Settings (CompanySettings).
    /// False when created by a user in User Email Settings.
    /// </summary>
    public bool IsServerManaged { get; set; }

    public int SortOrder { get; set; }

    /// <summary>
    /// Normalized lowercase receiving email addresses or domain patterns routed to this Virtual Inbox
    /// (e.g. "support@spokes.sh" or "@spokes.sh").
    /// </summary>
    public List<string> TargetAddresses { get; set; } = [];

    /// <summary>
    /// Individual Employee IDs granted access to view and triage this Virtual Inbox.
    /// </summary>
    public List<string> AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Team IDs granted access to view and triage this Virtual Inbox.
    /// </summary>
    public List<string> AllowedTeamIds { get; set; } = [];

    /// <summary>
    /// Sending Identity IDs linked to this Virtual Inbox. Users with access to this inbox can send/reply using these identities.
    /// </summary>
    public List<string> LinkedIdentityIds { get; set; } = [];

    /// <summary>
    /// Optional default Sending Identity ID when composing from this Virtual Inbox.
    /// </summary>
    public string? DefaultIdentityId { get; set; }

    /// <summary>
    /// When true, users replying in this Virtual Inbox may also select their own personal/shared Sending Identity
    /// as long as its domain matches the inbox's domain or the email's receiving address domain.
    /// </summary>
    public bool AllowDomainMatchedPersonalIdentity { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string? PrimaryAddress => TargetAddresses.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.Trim().StartsWith('@') && !a.Trim().StartsWith("*@"))
        ?? TargetAddresses.FirstOrDefault();

    [JsonIgnore]
    public string? PrimaryDomain
    {
        get
        {
            var addr = PrimaryAddress;
            if (string.IsNullOrWhiteSpace(addr)) return null;
            var trimmed = addr.Trim().TrimStart('*');
            var atIdx = trimmed.IndexOf('@');
            return atIdx >= 0 && atIdx < trimmed.Length - 1 ? trimmed[(atIdx + 1)..].Trim().ToLowerInvariant() : null;
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

    public bool MatchesAddresses(IEnumerable<string>? messageReceivingAddresses)
    {
        if (TargetAddresses == null || TargetAddresses.Count == 0 || messageReceivingAddresses == null) return false;

        foreach (var target in TargetAddresses)
        {
            if (string.IsNullOrWhiteSpace(target)) continue;
            var normalizedTarget = target.Trim().ToLowerInvariant();
            if (normalizedTarget.StartsWith("*@"))
            {
                normalizedTarget = normalizedTarget[1..]; // "@domain"
            }

            if (normalizedTarget.StartsWith('@') && normalizedTarget.Length > 1)
            {
                if (messageReceivingAddresses.Any(r =>
                        !string.IsNullOrWhiteSpace(r) &&
                        r.Trim().EndsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            else if (messageReceivingAddresses.Any(r => string.Equals(r?.Trim(), normalizedTarget, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }
}
