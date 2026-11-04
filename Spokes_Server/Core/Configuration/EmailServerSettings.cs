using System;
using System.Collections.Generic;
using System.Linq;

namespace Spokes_Server.Core.Configuration;

public class EmailServerSettings
{
    public List<IncomingServerConfig> IncomingServers { get; set; } = [];
    public List<SendingServerConfig> SendingServers { get; set; } = [];

    /// <summary>
    /// When true, non-admin users can add or edit Incoming and Outgoing server presets from their Email Settings.
    /// </summary>
    public bool AllowUsersToManageServers { get; set; } = false;

    /// <summary>
    /// When true, non-admin users can connect their own Real Inboxes (IMAP mailboxes).
    /// When false, only Administrators can provision Real Inboxes and share them with users/teams.
    /// </summary>
    public bool AllowUsersToConnectRealInboxes { get; set; } = true;

    /// <summary>
    /// When true, non-admin users can create Virtual Inboxes filtered from Real Inboxes they have access to.
    /// </summary>
    public bool AllowUsersToCreateVirtualInboxes { get; set; } = true;

    /// <summary>
    /// When true, non-admin users can create new Sending Identities on accessible Outgoing Servers.
    /// </summary>
    public bool AllowUsersToCreateSendingIdentities { get; set; } = true;

    /// <summary>
    /// When true, non-admin users can share Real Inboxes, Virtual Inboxes, and Sending Identities they own with other users and teams.
    /// </summary>
    public bool AllowUsersToShareEmailEntities { get; set; } = true;
}

public class IncomingServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// ID of the default Outgoing Server (SendingServerConfig) paired with this Incoming Server.
    /// Used when automatically creating a Sending Identity for a Real Inbox on this server.
    /// </summary>
    public string DefaultOutgoingServerId { get; set; } = string.Empty;

    public string OwnerEmployeeId { get; set; } = string.Empty;
    public bool IsSharedWithAllUsers { get; set; } = true;
    public List<string> AllowedUserIds { get; set; } = [];
    public List<string> AllowedTeamIds { get; set; } = [];

    public bool CanUserAccess(string employeeId, IEnumerable<string>? userTeamIds = null, bool isAdmin = false)
    {
        if (isAdmin || IsSharedWithAllUsers) return true;
        if (string.IsNullOrWhiteSpace(employeeId)) return false;
        if (string.Equals(OwnerEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)) return true;
        if (AllowedUserIds != null && AllowedUserIds.Any(u => string.Equals(u, employeeId, StringComparison.OrdinalIgnoreCase))) return true;
        if (userTeamIds != null && AllowedTeamIds != null && AllowedTeamIds.Intersect(userTeamIds, StringComparer.OrdinalIgnoreCase).Any()) return true;
        return false;
    }
}

public class SendingServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 465;
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// When true (e.g. Resend, Postmark, AWS SES), all Sending Identities on this server
    /// authenticate using SharedUsername and EncryptedSharedPassword.
    /// When false, each Sending Identity or Real Inbox provides its own SMTP credentials.
    /// </summary>
    public bool UseSharedCredentials { get; set; }
    public string SharedUsername { get; set; } = string.Empty;
    public string EncryptedSharedPassword { get; set; } = string.Empty;

    public string OwnerEmployeeId { get; set; } = string.Empty;
    public bool IsSharedWithAllUsers { get; set; } = true;
    public List<string> AllowedUserIds { get; set; } = [];
    public List<string> AllowedTeamIds { get; set; } = [];

    public bool CanUserAccess(string employeeId, IEnumerable<string>? userTeamIds = null, bool isAdmin = false)
    {
        if (isAdmin || IsSharedWithAllUsers) return true;
        if (string.IsNullOrWhiteSpace(employeeId)) return false;
        if (string.Equals(OwnerEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)) return true;
        if (AllowedUserIds != null && AllowedUserIds.Any(u => string.Equals(u, employeeId, StringComparison.OrdinalIgnoreCase))) return true;
        if (userTeamIds != null && AllowedTeamIds != null && AllowedTeamIds.Intersect(userTeamIds, StringComparer.OrdinalIgnoreCase).Any()) return true;
        return false;
    }
}
