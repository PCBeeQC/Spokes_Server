using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailInboxService : IEmailInboxService
{
    private readonly EmailAccountRepository _accounts;
    private readonly EmailInboxRepository _inboxes;
    private readonly EmailSendingIdentityRepository _identities;
    private readonly EmailFolderRepository _folders;
    private readonly EmailMessageRepository _messages;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly CompanyProfileRepository? _companyProfile;
    private readonly EmailSyncStateService? _syncStateService;
    private readonly EmailIdleService? _idleService;

    public EmailInboxService(
        EmailAccountRepository accounts,
        EmailInboxRepository inboxes,
        EmailSendingIdentityRepository identities,
        EmailFolderRepository folders,
        EmailMessageRepository messages,
        EmployeeRepository employees,
        TeamRepository teams,
        CompanyProfileRepository? companyProfile = null,
        EmailSyncStateService? syncStateService = null,
        EmailIdleService? idleService = null)
    {
        _accounts = accounts;
        _inboxes = inboxes;
        _identities = identities;
        _folders = folders;
        _messages = messages;
        _employees = employees;
        _teams = teams;
        _companyProfile = companyProfile;
        _syncStateService = syncStateService;
        _idleService = idleService;
    }

    public HashSet<string> GetUserTeamIds(string employeeId) =>
        TeamRepository.GetUserTeamIds(_teams, employeeId, string.IsNullOrWhiteSpace(employeeId) ? null : _employees?.GetById(employeeId));

    public List<EmailInbox> GetManageableInboxesForSettings(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _inboxes == null) return [];

        return _inboxes.GetAll()
            .Where(i => !i.IsServerManaged &&
                        !string.IsNullOrWhiteSpace(i.OwnerEmployeeId) &&
                        string.Equals(i.OwnerEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Name)
            .ToList();
    }

    private bool IsVirtualInboxBackingAccountAuthorized(EmailInbox inbox)
    {
        if (_accounts == null) return true;

        var account = _accounts.GetById(inbox.AccountId);
        if (account == null || account.IsArchived) return false;

        if (inbox.IsServerManaged)
        {
            return account.IsServerManaged;
        }

        if (string.IsNullOrWhiteSpace(inbox.OwnerEmployeeId)) return false;

        if (_employees?.GetById(inbox.OwnerEmployeeId) is { } ownerEmp &&
            (!ownerEmp.IsActive || ownerEmp.IsBanned))
        {
            return false;
        }

        return account.CanUserAccess(inbox.OwnerEmployeeId, GetUserTeamIds(inbox.OwnerEmployeeId));
    }

    private bool IsIdentitySourceBackingAuthorized(EmailSendingIdentity identity)
    {
        if (identity.SourceType == EmailIdentitySourceType.VirtualInbox &&
            !string.IsNullOrWhiteSpace(identity.SourceInboxId))
        {
            var sourceInbox = _inboxes?.GetById(identity.SourceInboxId);
            return sourceInbox != null && IsVirtualInboxBackingAccountAuthorized(sourceInbox);
        }

        return true;
    }

    private bool IsFamilyEdition() =>
        string.Equals(_companyProfile?.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase);

    private bool IsEmployeeEligibleForIdentities(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || IsFamilyEdition()) return false;

        if (_employees != null)
        {
            var emp = _employees.GetById(employeeId);
            if (emp == null || !emp.IsActive || emp.IsBanned || emp.IsSuspended || !FileAccessPermissionHelper.HasAssignedPermission(emp, AppPermissions.Email.Use))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsEmployeeEligibleForEmailRead(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || IsFamilyEdition()) return false;

        if (_employees != null)
        {
            var emp = _employees.GetById(employeeId);
            return FileAccessPermissionHelper.HasAssignedPermission(emp, AppPermissions.Email.Use);
        }

        return true;
    }

    private EmailFolder? GetPrimaryRootInboxFolder(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || _folders == null) return null;

        var folders = _folders.GetByAccount(accountId);
        return folders.FirstOrDefault(f => f.IsInbox && string.IsNullOrEmpty(f.ParentPath))
               ?? folders.FirstOrDefault(f => string.IsNullOrEmpty(f.ParentPath) && f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
    }

    public bool IsPrimaryRootInboxMessage(EmailMessage message)
    {
        if (message == null || string.IsNullOrWhiteSpace(message.AccountId) || string.IsNullOrWhiteSpace(message.FolderPath))
            return false;

        var rootInbox = GetPrimaryRootInboxFolder(message.AccountId);
        if (rootInbox != null)
        {
            return string.Equals(message.FolderPath, rootInbox.Path, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(message.FolderPath.Trim(), "INBOX", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if a mailbox (real account or virtual inbox backing account) may legitimately
    /// reference and store copies/drafts for the given sending identity via LinkedIdentityIds / DefaultIdentityId.
    /// Prevents admins from binding a user's private unshared personal OutgoingServer identity into
    /// a server-managed or third-party mailbox so Sent copies cannot leak into shared storage.
    /// </summary>
    public static bool CanMailboxLinkIdentity(
        EmailSendingIdentity? identity,
        bool mailboxIsServerManaged,
        string? mailboxOwnerEmployeeId,
        IReadOnlyCollection<string>? mailboxOwnerTeamIds = null)
    {
        if (identity == null) return false;

        if (identity.IsServerManaged)
        {
            return mailboxIsServerManaged ||
                   (!string.IsNullOrWhiteSpace(mailboxOwnerEmployeeId) &&
                    identity.CanUserAccess(mailboxOwnerEmployeeId, mailboxOwnerTeamIds));
        }

        if (!mailboxIsServerManaged &&
            !string.IsNullOrWhiteSpace(mailboxOwnerEmployeeId) &&
            string.Equals(mailboxOwnerEmployeeId, identity.OwnerEmployeeId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (identity.AllowedUserIds?.Count > 0) || (identity.AllowedTeamIds?.Count > 0);
    }

    public List<EmailInbox> GetAccessibleInboxes(Employee employee) =>
        employee == null ? [] : GetAccessibleInboxes(employee.Id);

    public List<EmailInbox> GetAccessibleInboxes(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _inboxes == null || !IsEmployeeEligibleForEmailRead(employeeId)) return [];
        var teamIds = GetUserTeamIds(employeeId);

        return _inboxes.GetAccessibleForUser(employeeId, teamIds)
            .Where(IsVirtualInboxBackingAccountAuthorized)
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Name)
            .ToList();
    }

    public List<EmailAccount> GetAccessibleRealInboxes(Employee employee, bool includeArchived = false) =>
        employee == null ? [] : GetAccessibleRealInboxes(employee.Id, includeArchived);

    public List<EmailAccount> GetAccessibleRealInboxes(string employeeId, bool includeArchived = false)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _accounts == null || !IsEmployeeEligibleForEmailRead(employeeId)) return [];
        var teamIds = GetUserTeamIds(employeeId);

        return _accounts.GetAccessibleForUser(employeeId, teamIds, includeArchived);
    }

    public List<EmailAccount> GetAccessibleArchivedRealInboxes(Employee employee) =>
        employee == null ? [] : GetAccessibleArchivedRealInboxes(employee.Id);

    public List<EmailAccount> GetAccessibleArchivedRealInboxes(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _accounts == null || !IsEmployeeEligibleForEmailRead(employeeId)) return [];
        var teamIds = GetUserTeamIds(employeeId);

        return _accounts.GetArchivedAccessibleForUser(employeeId, teamIds);
    }

    public List<EmailAccount> GetAccessibleAccounts(Employee employee, bool includeArchived = false) =>
        employee == null ? [] : GetAccessibleAccounts(employee.Id, includeArchived);

    public List<EmailAccount> GetAccessibleAccounts(string employeeId, bool includeArchived = false)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _accounts == null || !IsEmployeeEligibleForEmailRead(employeeId)) return [];

        var teamIds = GetUserTeamIds(employeeId);
        var inboxes = GetAccessibleInboxes(employeeId);
        var realInboxes = _accounts.GetAccessibleForUser(employeeId, teamIds, includeArchived);
        var accountIds = inboxes.Select(i => i.AccountId)
            .Concat(realInboxes.Select(a => a.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _accounts.GetAll()
            .Where(a => (includeArchived || !a.IsArchived) && accountIds.Contains(a.Id))
            .ToList();
    }

    public bool HasWebmailConsumers(EmailAccount account) =>
        HasWebmailConsumers(account, _inboxes);

    /// <summary>
    /// Returns true when the mailbox has at least one Webmail (/email) consumer:
    /// either it is a personal employee mailbox, or it is shared directly with users/teams,
    /// or it hosts at least one Virtual Inbox accessible in Webmail.
    /// Server-managed Real Inboxes used exclusively by automated services (e.g., Company Bill Inbox)
    /// return false so full-mailbox Webmail sync does not download historical read messages.
    /// </summary>
    public static bool HasWebmailConsumers(EmailAccount? account, EmailInboxRepository? inboxes = null)
    {
        if (account == null)
        {
            return false;
        }

        if (!account.IsServerManaged)
        {
            return true;
        }

        if ((account.AllowedUserIds?.Count > 0) || (account.AllowedTeamIds?.Count > 0))
        {
            return true;
        }

        if (inboxes != null)
        {
            return inboxes.GetByAccount(account.Id)
                .Any(i => !i.IsServerManaged || (i.AllowedUserIds?.Count > 0) || (i.AllowedTeamIds?.Count > 0));
        }

        return false;
    }

    public Func<MessageMetadata, bool>? GetMetadataFilterForInbox(EmailInbox inbox)
    {
        if (inbox.TargetAddresses == null || inbox.TargetAddresses.Count == 0)
            return _ => false;

        return meta => inbox.MatchesAddresses(meta.ReceivingAddresses);
    }

    public bool DoesMessageMatchInbox(EmailMessage message, EmailInbox inbox)
    {
        if (!string.Equals(message.AccountId, inbox.AccountId, StringComparison.OrdinalIgnoreCase))
            return false;

        var receiving = message.ReceivingAddresses?.Count > 0
            ? message.ReceivingAddresses
            : message.ToAddresses.Concat(message.CcAddresses).Select(a => a.Trim().ToLowerInvariant()).ToList();

        return inbox.MatchesAddresses(receiving);
    }

    public List<string> GetUnmatchedAddressesForMessage(EmailMessage message)
    {
        var account = _accounts?.GetById(message.AccountId);
        var accountEmail = account?.EmailAddress?.Trim().ToLowerInvariant();
        var accountInboxes = _inboxes.GetByAccount(message.AccountId);

        return ExtractForwardedOrAliasCandidates(message, accountEmail)
            .Where(a => !accountInboxes.Any(inbox => inbox.MatchesAddresses([a])))
            .OrderBy(a => a)
            .ToList();
    }

    public List<string> GetDetectedAddressesForAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return [];

        var account = _accounts?.GetById(accountId);
        var accountEmail = account?.EmailAddress?.Trim().ToLowerInvariant();
        var inboxFolder = GetPrimaryRootInboxFolder(accountId);
        if (inboxFolder == null) return [];

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var msg in _messages.GetByFolder(accountId, inboxFolder.Path))
        {
            foreach (var addr in ExtractForwardedOrAliasCandidates(msg, accountEmail))
            {
                set.Add(addr);
            }
        }

        return set.OrderBy(a => a).ToList();
    }

    private static IEnumerable<string> ExtractForwardedOrAliasCandidates(EmailMessage msg, string? accountEmail)
    {
        var fromAddr = msg.FromAddress?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(accountEmail) &&
            string.Equals(fromAddr, accountEmail, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var toSet = (msg.ToAddresses ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ccSet = (msg.CcAddresses ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var receivingSet = (msg.ReceivingAddresses ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Any address in ReceivingAddresses that was NOT merely a To/Cc header
        // (i.e. came from forwarding/envelope headers like X-Forwarded-For, Delivered-To, Received: for <...>)
        foreach (var addr in receivingSet)
        {
            if (!toSet.Contains(addr) && !ccSet.Contains(addr))
            {
                candidates.Add(addr);
            }
        }

        // 2. If the Real Inbox's primary email is NOT a direct To/Cc recipient, the message was forwarded/routed
        // into this inbox for its To recipients (or ReceivingAddresses when To/Cc are empty)
        bool directlyAddressedToAccount = !string.IsNullOrWhiteSpace(accountEmail) &&
                                          (toSet.Contains(accountEmail) || ccSet.Contains(accountEmail));

        if (!directlyAddressedToAccount)
        {
            foreach (var addr in toSet)
            {
                candidates.Add(addr);
            }

            if (toSet.Count == 0 && ccSet.Count == 0)
            {
                foreach (var addr in receivingSet)
                {
                    candidates.Add(addr);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(accountEmail))
        {
            candidates.Remove(accountEmail);
        }
        if (!string.IsNullOrWhiteSpace(fromAddr))
        {
            candidates.Remove(fromAddr);
        }

        return candidates;
    }

    public List<string> GetUnmatchedAddressesForAccount(string accountId)
    {
        var accountInboxes = _inboxes.GetByAccount(accountId);

        return GetDetectedAddressesForAccount(accountId)
            .Where(a => !accountInboxes.Any(inbox => inbox.MatchesAddresses([a])))
            .ToList();
    }

    public int GetUnreadCountForInbox(EmailInbox inbox, string? folderPath = null)
    {
        var filter = GetMetadataFilterForInbox(inbox);
        if (!string.IsNullOrEmpty(folderPath))
        {
            return _messages.GetUnreadCount(inbox.AccountId, folderPath, filter);
        }

        var inboxFolder = GetPrimaryRootInboxFolder(inbox.AccountId);
        if (inboxFolder == null) return 0;

        return _messages.GetUnreadCount(inbox.AccountId, inboxFolder.Path, filter);
    }

    public int GetUnreadCountForRealInbox(string accountId, string? folderPath = null)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return 0;
        if (!string.IsNullOrEmpty(folderPath))
        {
            return _messages.GetUnreadCount(accountId, folderPath);
        }

        var inboxFolder = GetPrimaryRootInboxFolder(accountId);
        if (inboxFolder == null) return 0;

        return _messages.GetUnreadCount(accountId, inboxFolder.Path);
    }

    public int GetTotalUnreadCountForUser(string employeeId) =>
        !IsEmployeeEligibleForEmailRead(employeeId) ? 0 : GetUnreadCountForUserCore(employeeId, sinceUtc: null);

    public int GetUnseenUnreadCountForUser(string employeeId)
    {
        if (!IsEmployeeEligibleForEmailRead(employeeId)) return 0;
        var employee = _employees?.GetById(employeeId);
        return GetUnreadCountForUserCore(employeeId, employee?.LastEmailVisitedAt);
    }

    public void MarkEmailPageVisited(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _employees == null) return;

        var employee = _employees.GetById(employeeId);
        if (employee == null) return;

        var now = DateTimeOffset.UtcNow;
        var latestUnreadDate = GetLatestUnreadInboxMessageDate(employeeId);
        var watermark = latestUnreadDate.HasValue && latestUnreadDate.Value > now
            ? latestUnreadDate.Value
            : now;

        if (employee.LastEmailVisitedAt.HasValue &&
            watermark <= employee.LastEmailVisitedAt.Value.AddSeconds(5) &&
            GetUnreadCountForUserCore(employeeId, employee.LastEmailVisitedAt) == 0)
        {
            return;
        }

        employee.LastEmailVisitedAt = watermark;
        _employees.Save(employee);
    }

    private IEnumerable<MessageMetadata> EnumerateAccessibleUnreadInboxMetadata(string employeeId)
    {
        var realInboxes = GetAccessibleRealInboxes(employeeId);
        var virtualInboxes = GetAccessibleInboxes(employeeId);
        if (realInboxes.Count == 0 && virtualInboxes.Count == 0) yield break;

        foreach (var account in realInboxes)
        {
            var inboxFolder = GetPrimaryRootInboxFolder(account.Id);
            if (inboxFolder == null) continue;

            foreach (var meta in _messages.GetFolderMetadata(account.Id, inboxFolder.Path))
            {
                if (!meta.IsRead)
                {
                    yield return meta;
                }
            }
        }

        foreach (var inbox in virtualInboxes)
        {
            var inboxFolder = GetPrimaryRootInboxFolder(inbox.AccountId);
            if (inboxFolder == null) continue;

            var filter = GetMetadataFilterForInbox(inbox);
            foreach (var meta in _messages.GetFolderMetadata(inbox.AccountId, inboxFolder.Path))
            {
                if (!meta.IsRead && (filter == null || filter(meta)))
                {
                    yield return meta;
                }
            }
        }
    }

    private int GetUnreadCountForUserCore(string employeeId, DateTimeOffset? sinceUtc)
    {
        var unreadMessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in EnumerateAccessibleUnreadInboxMetadata(employeeId))
        {
            if (!sinceUtc.HasValue || meta.Date > sinceUtc.Value)
            {
                unreadMessageIds.Add(meta.Id);
            }
        }
        return unreadMessageIds.Count;
    }

    private DateTimeOffset? GetLatestUnreadInboxMessageDate(string employeeId)
    {
        DateTimeOffset? latest = null;
        foreach (var meta in EnumerateAccessibleUnreadInboxMetadata(employeeId))
        {
            if (!latest.HasValue || meta.Date > latest.Value)
            {
                latest = meta.Date;
            }
        }
        return latest;
    }

    public List<EmailMessage> GetInboxMessages(EmailInbox inbox, int count = 50, DateTime? beforeDateUtc = null)
    {
        var inboxFolder = GetPrimaryRootInboxFolder(inbox.AccountId);
        if (inboxFolder == null) return [];

        var filter = GetMetadataFilterForInbox(inbox);
        return beforeDateUtc.HasValue
            ? _messages.GetByFolderBeforeDate(inbox.AccountId, inboxFolder.Path, beforeDateUtc.Value, count, filter)
            : _messages.GetByFolderRecent(inbox.AccountId, inboxFolder.Path, count, filter);
    }

    public List<EmailMessage> GetRealInboxMessages(string accountId, int count = 50, DateTime? beforeDateUtc = null)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return [];
        var inboxFolder = GetPrimaryRootInboxFolder(accountId);
        if (inboxFolder == null) return [];

        return beforeDateUtc.HasValue
            ? _messages.GetByFolderBeforeDate(accountId, inboxFolder.Path, beforeDateUtc.Value, count)
            : _messages.GetByFolderRecent(accountId, inboxFolder.Path, count);
    }

    public List<EmailMessage> GetAllInboxesMessages(Employee employee, int count = 50, DateTime? beforeDateUtc = null)
    {
        if (employee == null) return [];

        var realInboxes = GetAccessibleRealInboxes(employee.Id);
        var virtualInboxes = GetAccessibleInboxes(employee.Id);
        var resultById = new Dictionary<string, EmailMessage>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in realInboxes)
        {
            foreach (var msg in GetRealInboxMessages(account.Id, count, beforeDateUtc))
            {
                resultById[msg.Id] = msg;
            }
        }

        foreach (var inbox in virtualInboxes)
        {
            foreach (var msg in GetInboxMessages(inbox, count, beforeDateUtc))
            {
                resultById[msg.Id] = msg;
            }
        }

        if (realInboxes.Count == 0 && virtualInboxes.Count == 0)
        {
            foreach (var account in GetAccessibleAccounts(employee.Id))
            {
                foreach (var msg in GetRealInboxMessages(account.Id, count, beforeDateUtc))
                {
                    resultById[msg.Id] = msg;
                }
            }
        }

        return resultById.Values
            .OrderByDescending(m => m.Date)
            .Take(count)
            .ToList();
    }

    public EmailSendingIdentity EnsureRealInboxSendingIdentity(EmailAccount account, string? displayName = null)
    {
        if (account == null) throw new ArgumentNullException(nameof(account));

        var settings = _companyProfile?.Get()?.EmailSettings;
        var incomingServer = !string.IsNullOrWhiteSpace(account.IncomingServerConfigId)
            ? settings?.IncomingServers?.FirstOrDefault(s => string.Equals(s.Id, account.IncomingServerConfigId, StringComparison.OrdinalIgnoreCase))
            : null;

        var (localUser, domain) = SplitEmailAddress(account.EmailAddress);

        var outgoingServerId = !string.IsNullOrWhiteSpace(account.OutgoingServerConfigId)
            ? account.OutgoingServerConfigId
            : (incomingServer?.DefaultOutgoingServerId ?? string.Empty);

        if (string.IsNullOrWhiteSpace(outgoingServerId))
        {
            var matchDomain = !string.IsNullOrWhiteSpace(incomingServer?.Domain)
                ? incomingServer.Domain.Trim().TrimStart('@')
                : domain;
            if (!string.IsNullOrWhiteSpace(matchDomain))
            {
                outgoingServerId = settings?.SendingServers?.FirstOrDefault(s =>
                    !string.IsNullOrWhiteSpace(s.Domain) &&
                    string.Equals(s.Domain.Trim().TrimStart('@'), matchDomain, StringComparison.OrdinalIgnoreCase))?.Id ?? string.Empty;
            }
        }

        EmailSendingIdentity? identity = null;
        if (!string.IsNullOrWhiteSpace(account.AutoCreatedIdentityId))
        {
            identity = _identities.GetById(account.AutoCreatedIdentityId);
        }

        identity ??= _identities.GetAll().FirstOrDefault(i =>
            i.SourceType == EmailIdentitySourceType.RealInbox &&
            string.Equals(i.SourceAccountId, account.Id, StringComparison.OrdinalIgnoreCase));

        var ownerEmployee = !string.IsNullOrWhiteSpace(account.OwnerEmployeeId)
            ? _employees?.GetById(account.OwnerEmployeeId)
            : null;
        var fallbackSenderName = !string.IsNullOrWhiteSpace(ownerEmployee?.FullName)
            ? ownerEmployee.FullName.Trim()
            : (!string.IsNullOrWhiteSpace(_companyProfile?.Get()?.CompanyName)
                ? _companyProfile.Get()!.CompanyName.Trim()
                : account.EmailAddress);

        if (identity == null)
        {
            var resolvedDisplayName = !string.IsNullOrWhiteSpace(displayName)
                ? displayName.Trim()
                : fallbackSenderName;

            identity = new EmailSendingIdentity
            {
                OwnerEmployeeId = account.OwnerEmployeeId,
                IsServerManaged = account.IsServerManaged,
                SourceType = EmailIdentitySourceType.RealInbox,
                SourceAccountId = account.Id,
                SendingServerConfigId = outgoingServerId,
                DisplayName = resolvedDisplayName,
                Username = localUser,
                Domain = domain,
                CustomSmtpHost = account.CustomSmtpHost,
                CustomSmtpPort = account.CustomSmtpPort > 0 ? account.CustomSmtpPort : 587,
                CustomSmtpSsl = account.CustomSmtpSsl,
                AllowedUserIds = new List<string>(account.AllowedUserIds ?? []),
                AllowedTeamIds = new List<string>(account.AllowedTeamIds ?? [])
            };
        }
        else
        {
            identity.OwnerEmployeeId = account.OwnerEmployeeId;
            identity.IsServerManaged = account.IsServerManaged;
            identity.SourceType = EmailIdentitySourceType.RealInbox;
            identity.SourceAccountId = account.Id;
            if (!string.IsNullOrWhiteSpace(outgoingServerId))
            {
                identity.SendingServerConfigId = outgoingServerId;
            }
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                identity.DisplayName = displayName.Trim();
            }
            else if (string.IsNullOrWhiteSpace(identity.DisplayName))
            {
                identity.DisplayName = fallbackSenderName;
            }
            if (!string.IsNullOrWhiteSpace(localUser)) identity.Username = localUser;
            if (!string.IsNullOrWhiteSpace(domain)) identity.Domain = domain;
            if (!string.IsNullOrWhiteSpace(account.CustomSmtpHost))
            {
                identity.CustomSmtpHost = account.CustomSmtpHost;
                identity.CustomSmtpPort = account.CustomSmtpPort > 0 ? account.CustomSmtpPort : 587;
                identity.CustomSmtpSsl = account.CustomSmtpSsl;
            }
            identity.AllowedUserIds = new List<string>(account.AllowedUserIds ?? []);
            identity.AllowedTeamIds = new List<string>(account.AllowedTeamIds ?? []);
        }

        _identities.Save(identity);

        account.OutgoingServerConfigId = outgoingServerId;
        account.AutoCreatedIdentityId = identity.Id;
        account.LinkedIdentityIds ??= [];
        if (!account.LinkedIdentityIds.Contains(identity.Id, StringComparer.OrdinalIgnoreCase))
        {
            account.LinkedIdentityIds.Add(identity.Id);
        }
        if (string.IsNullOrWhiteSpace(account.DefaultIdentityId))
        {
            account.DefaultIdentityId = identity.Id;
        }
        _accounts.Save(account);

        return identity;
    }

    private static void AddMailboxLinkedIdentities(
        Dictionary<string, EmailSendingIdentity> target,
        IReadOnlyDictionary<string, EmailSendingIdentity> allIdentitiesById,
        bool mailboxIsServerManaged,
        string? mailboxOwnerEmployeeId,
        string? defaultIdentityId,
        IEnumerable<string>? linkedIdentityIds,
        string? autoCreatedIdentityId = null)
    {
        void TryAdd(string? identityId)
        {
            if (!string.IsNullOrWhiteSpace(identityId) &&
                allIdentitiesById.TryGetValue(identityId, out var identity) &&
                CanMailboxLinkIdentity(identity, mailboxIsServerManaged, mailboxOwnerEmployeeId))
            {
                target[identity.Id] = identity;
            }
        }

        TryAdd(defaultIdentityId);
        TryAdd(autoCreatedIdentityId);
        if (linkedIdentityIds != null)
        {
            foreach (var linkedId in linkedIdentityIds)
            {
                TryAdd(linkedId);
            }
        }
    }

    public List<EmailSendingIdentity> GetPermittedIdentities(Employee employee, EmailInbox? inbox = null, IEnumerable<string>? replyReceivingAddresses = null, EmailAccount? realInbox = null) =>
        employee == null ? [] : GetPermittedIdentities(employee.Id, inbox, replyReceivingAddresses, realInbox);

    public List<EmailSendingIdentity> GetPermittedIdentities(string employeeId, EmailInbox? inbox = null, IEnumerable<string>? replyReceivingAddresses = null, EmailAccount? realInbox = null)
    {
        if (!IsEmployeeEligibleForIdentities(employeeId))
        {
            return [];
        }

        var teamIds = GetUserTeamIds(employeeId);
        var archivedAccountIds = _accounts != null
            ? _accounts.GetAll().Where(a => a.IsArchived).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allIdentities = _identities.GetAll()
            .Where(i => (string.IsNullOrWhiteSpace(i.SourceAccountId) || !archivedAccountIds.Contains(i.SourceAccountId)) &&
                        IsIdentitySourceBackingAuthorized(i))
            .ToList();
        var allIdentitiesById = new Dictionary<string, EmailSendingIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in allIdentities)
        {
            if (!string.IsNullOrWhiteSpace(identity.Id) && !allIdentitiesById.ContainsKey(identity.Id))
            {
                allIdentitiesById[identity.Id] = identity;
            }
        }
        var userAccessibleIdentities = _identities.GetAccessibleForUser(employeeId, teamIds)
            .Where(i => (string.IsNullOrWhiteSpace(i.SourceAccountId) || !archivedAccountIds.Contains(i.SourceAccountId)) &&
                        IsIdentitySourceBackingAuthorized(i))
            .ToList();

        if (inbox == null && realInbox == null)
        {
            var accessibleInboxes = GetAccessibleInboxes(employeeId)
                .Where(i => string.IsNullOrWhiteSpace(i.AccountId) || !archivedAccountIds.Contains(i.AccountId))
                .ToList();
            var accessibleRealInboxes = GetAccessibleRealInboxes(employeeId);
            var accessibleInboxIds = accessibleInboxes.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var accessibleRealInboxIds = accessibleRealInboxes.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var aggregated = new Dictionary<string, EmailSendingIdentity>(StringComparer.OrdinalIgnoreCase);

            foreach (var accessibleInbox in accessibleInboxes)
            {
                AddMailboxLinkedIdentities(aggregated, allIdentitiesById, accessibleInbox.IsServerManaged, accessibleInbox.OwnerEmployeeId,
                    accessibleInbox.DefaultIdentityId, accessibleInbox.LinkedIdentityIds);
            }

            foreach (var accessibleRealInbox in accessibleRealInboxes)
            {
                AddMailboxLinkedIdentities(aggregated, allIdentitiesById, accessibleRealInbox.IsServerManaged, accessibleRealInbox.OwnerEmployeeId,
                    accessibleRealInbox.DefaultIdentityId, accessibleRealInbox.LinkedIdentityIds, accessibleRealInbox.AutoCreatedIdentityId);
            }

            foreach (var identity in allIdentities)
            {
                if ((identity.SourceType == EmailIdentitySourceType.VirtualInbox &&
                     !string.IsNullOrWhiteSpace(identity.SourceInboxId) &&
                     accessibleInboxIds.Contains(identity.SourceInboxId)) ||
                    (identity.SourceType == EmailIdentitySourceType.RealInbox &&
                     !string.IsNullOrWhiteSpace(identity.SourceAccountId) &&
                     accessibleRealInboxIds.Contains(identity.SourceAccountId)) ||
                    identity.CanUserAccess(employeeId, teamIds))
                {
                    aggregated[identity.Id] = identity;
                }
            }

            return aggregated.Values
                .OrderBy(id => id.DisplayName)
                .ThenBy(id => id.EmailAddress)
                .ToList();
        }

        var result = new Dictionary<string, EmailSendingIdentity>(StringComparer.OrdinalIgnoreCase);

        // 1. Process Virtual Inbox if specified, accessible to the user, and its backing account is authorized
        if (inbox != null && IsVirtualInboxBackingAccountAuthorized(inbox) && inbox.CanUserAccess(employeeId, teamIds))
        {
            AddMailboxLinkedIdentities(result, allIdentitiesById, inbox.IsServerManaged, inbox.OwnerEmployeeId,
                inbox.DefaultIdentityId, inbox.LinkedIdentityIds);

            var inboxSourceIdentities = allIdentities.Where(i =>
                i.SourceType == EmailIdentitySourceType.VirtualInbox &&
                string.Equals(i.SourceInboxId, inbox.Id, StringComparison.OrdinalIgnoreCase));
            foreach (var sid in inboxSourceIdentities)
            {
                result[sid.Id] = sid;
            }

            if (inbox.AllowDomainMatchedPersonalIdentity)
            {
                var mailboxDomains = (inbox.TargetAddresses ?? []).Select(ExtractDomain);
                AddDomainMatchedPersonalIdentities(result, userAccessibleIdentities, mailboxDomains, replyReceivingAddresses);
            }
        }

        // 2. Process Real Inbox if specified and accessible to the user
        if (realInbox != null)
        {
            if (!realInbox.IsArchived && realInbox.CanUserAccess(employeeId, teamIds))
            {
                AddMailboxLinkedIdentities(result, allIdentitiesById, realInbox.IsServerManaged, realInbox.OwnerEmployeeId,
                    realInbox.DefaultIdentityId, realInbox.LinkedIdentityIds, realInbox.AutoCreatedIdentityId);

                if (realInbox.AllowDomainMatchedPersonalIdentity && inbox == null)
                {
                    var mailboxDomains = !string.IsNullOrWhiteSpace(realInbox.PrimaryDomain)
                        ? new[] { realInbox.PrimaryDomain }
                        : Array.Empty<string>();
                    AddDomainMatchedPersonalIdentities(result, userAccessibleIdentities, mailboxDomains, replyReceivingAddresses);
                }
            }
        }

        if (result.Count == 0)
        {
            foreach (var personalId in userAccessibleIdentities)
            {
                result[personalId.Id] = personalId;
            }
        }

        return result.Values
            .OrderBy(id => id.DisplayName)
            .ThenBy(id => id.EmailAddress)
            .ToList();
    }

    public bool IsIdentityPermittedForUser(string employeeId, string identityId, string? inboxId = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(identityId))
            return false;

        var identity = _identities.GetById(identityId);
        return identity != null && IsIdentityPermittedForUser(employeeId, identity, inboxId);
    }

    public bool IsIdentityPermittedForUser(string employeeId, EmailSendingIdentity identity, string? inboxId = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || identity == null || string.IsNullOrWhiteSpace(identity.Id))
            return false;

        if (!IsEmployeeEligibleForIdentities(employeeId))
            return false;

        if (!string.IsNullOrWhiteSpace(inboxId))
        {
            var inbox = _inboxes?.GetById(inboxId);
            if (inbox == null || !IsVirtualInboxBackingAccountAuthorized(inbox) || !inbox.CanUserAccess(employeeId, GetUserTeamIds(employeeId)))
            {
                return false;
            }

            return GetPermittedIdentities(employeeId, inbox: inbox)
                .Any(i => string.Equals(i.Id, identity.Id, StringComparison.OrdinalIgnoreCase));
        }

        return GetPermittedIdentities(employeeId, inbox: null, realInbox: null)
            .Any(i => string.Equals(i.Id, identity.Id, StringComparison.OrdinalIgnoreCase));
    }

    public EmailSendingIdentity? SelectDefaultIdentity(Employee employee, EmailInbox? inbox = null, IEnumerable<string>? replyReceivingAddresses = null, EmailAccount? realInbox = null)
    {
        if (employee == null) return null;
        var permitted = GetPermittedIdentities(employee.Id, inbox, replyReceivingAddresses, realInbox);
        EmailMessage? synthMsg = replyReceivingAddresses != null
            ? new EmailMessage { ReceivingAddresses = replyReceivingAddresses.ToList() }
            : null;
        return SelectDefaultIdentity(permitted, employee.Id, inbox, synthMsg, realInbox);
    }

    public EmailSendingIdentity? SelectDefaultIdentity(IReadOnlyList<EmailSendingIdentity> permittedIdentities, string employeeId, EmailInbox? inbox = null, EmailMessage? replyToMessage = null, EmailAccount? realInbox = null)
    {
        if (permittedIdentities == null || permittedIdentities.Count == 0) return null;

        // 1. Reply-to message address matching takes top priority when replying
        if (replyToMessage != null)
        {
            var receiving = replyToMessage.ReceivingAddresses is { Count: > 0 }
                ? replyToMessage.ReceivingAddresses
                : replyToMessage.ToAddresses.Concat(replyToMessage.CcAddresses);

            var exactMatch = permittedIdentities.FirstOrDefault(id =>
                receiving.Any(r => string.Equals(r?.Trim(), id.EmailAddress, StringComparison.OrdinalIgnoreCase)));
            if (exactMatch != null) return exactMatch;
        }

        // 2. Virtual Inbox takes priority when the active context is a Virtual Inbox
        if (inbox != null)
        {
            if (!string.IsNullOrWhiteSpace(inbox.DefaultIdentityId))
            {
                var defaultId = permittedIdentities.FirstOrDefault(id => string.Equals(id.Id, inbox.DefaultIdentityId, StringComparison.OrdinalIgnoreCase));
                if (defaultId != null) return defaultId;
            }

            if (inbox.LinkedIdentityIds is { Count: > 0 })
            {
                var firstLinked = permittedIdentities.FirstOrDefault(id => inbox.LinkedIdentityIds.Contains(id.Id));
                if (firstLinked != null) return firstLinked;
            }

            var inboxSource = permittedIdentities.FirstOrDefault(id =>
                id.SourceType == EmailIdentitySourceType.VirtualInbox &&
                string.Equals(id.SourceInboxId, inbox.Id, StringComparison.OrdinalIgnoreCase));
            if (inboxSource != null) return inboxSource;

            if (inbox.TargetAddresses is { Count: > 0 })
            {
                var targetMatch = permittedIdentities.FirstOrDefault(id =>
                    inbox.TargetAddresses.Any(t => string.Equals(t?.Trim(), id.EmailAddress, StringComparison.OrdinalIgnoreCase)));
                if (targetMatch != null) return targetMatch;
            }

            if (!string.IsNullOrWhiteSpace(inbox.PrimaryDomain))
            {
                var domainMatch = permittedIdentities.FirstOrDefault(id =>
                    string.Equals(id.EffectiveDomain, inbox.PrimaryDomain, StringComparison.OrdinalIgnoreCase));
                if (domainMatch != null) return domainMatch;
            }
        }

        // 3. Real Inbox selection
        if (realInbox != null)
        {
            if (!string.IsNullOrWhiteSpace(realInbox.DefaultIdentityId))
            {
                var defaultId = permittedIdentities.FirstOrDefault(id => string.Equals(id.Id, realInbox.DefaultIdentityId, StringComparison.OrdinalIgnoreCase));
                if (defaultId != null) return defaultId;
            }

            if (!string.IsNullOrWhiteSpace(realInbox.AutoCreatedIdentityId))
            {
                var autoId = permittedIdentities.FirstOrDefault(id => string.Equals(id.Id, realInbox.AutoCreatedIdentityId, StringComparison.OrdinalIgnoreCase));
                if (autoId != null) return autoId;
            }

            if (realInbox.LinkedIdentityIds is { Count: > 0 })
            {
                var firstLinked = permittedIdentities.FirstOrDefault(id => realInbox.LinkedIdentityIds.Contains(id.Id));
                if (firstLinked != null) return firstLinked;
            }

            var emailMatch = permittedIdentities.FirstOrDefault(id => string.Equals(id.EmailAddress, realInbox.EmailAddress, StringComparison.OrdinalIgnoreCase));
            if (emailMatch != null) return emailMatch;
        }

        // 4. Default to employee owned identity, then first permitted
        var owned = permittedIdentities.FirstOrDefault(id => string.Equals(id.OwnerEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase));
        return owned ?? permittedIdentities[0];
    }

    public List<string> GetAuthorizedEmployeeIdsForMessage(EmailMessage message)
    {
        if (message == null || IsFamilyEdition())
            return [];

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var account = _accounts?.GetById(message.AccountId);
        if (account is { IsArchived: true })
            return [];

        var allEmployees = (_employees?.GetAll() ?? [])
            .Where(e => e.IsActive && !e.IsSuspended && !e.IsBanned &&
                        FileAccessPermissionHelper.HasAssignedPermission(e, AppPermissions.Email.Use))
            .ToList();

        if (account != null)
        {
            if (!account.IsServerManaged &&
                !string.IsNullOrWhiteSpace(account.OwnerEmployeeId) &&
                (_employees == null ||
                 (_employees.GetById(account.OwnerEmployeeId) is { IsSuspended: false } ownerEmp &&
                  FileAccessPermissionHelper.HasAssignedPermission(ownerEmp, AppPermissions.Email.Use))))
            {
                result.Add(account.OwnerEmployeeId);
            }

            foreach (var emp in allEmployees)
            {
                var teamIds = GetUserTeamIds(emp.Id);
                if (account.CanUserAccess(emp.Id, teamIds))
                {
                    result.Add(emp.Id);
                }
            }
        }

        if (IsLocalDraftMessage(message) || IsPrimaryRootInboxMessage(message))
        {
            var accountInboxes = _inboxes?.GetByAccount(message.AccountId) ?? [];

            foreach (var inbox in accountInboxes)
            {
                if (!IsVirtualInboxBackingAccountAuthorized(inbox))
                {
                    continue;
                }

                if (DoesMessageMatchInbox(message, inbox))
                {
                    foreach (var emp in allEmployees)
                    {
                        var teamIds = GetUserTeamIds(emp.Id);
                        if (inbox.CanUserAccess(emp.Id, teamIds))
                        {
                            result.Add(emp.Id);
                        }
                    }
                }
            }
        }

        if (IsLocalDraftMessage(message))
        {
            return result
                .Where(empId => CanEmployeeAccessLocalDraft(message, empId, account, message.AccountId))
                .ToList();
        }

        return result.ToList();
    }

    private static bool IsLocalDraftMessage(EmailMessage msg) =>
        string.Equals(msg.FolderPath, "LocalDrafts", StringComparison.OrdinalIgnoreCase);

    public bool CanEmployeeAccessLocalDraft(EmailMessage draft, string employeeId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return CanEmployeeAccessLocalDraft(draft, employeeId, _accounts?.GetById(draft.AccountId), draft.AccountId);
    }

    public static bool CanEmployeeAccessLocalDraft(EmailMessage draft, string employeeId, EmailAccount? account, string accountId)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return false;

        if (!string.IsNullOrWhiteSpace(draft.CreatedByEmployeeId))
        {
            return string.Equals(draft.CreatedByEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase);
        }

        // Legacy drafts without CreatedByEmployeeId are strictly restricted to the personal (non-server-managed) account owner.
        if (account != null)
        {
            return !account.IsServerManaged &&
                   !string.IsNullOrWhiteSpace(account.OwnerEmployeeId) &&
                   string.Equals(account.OwnerEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(accountId, employeeId, StringComparison.OrdinalIgnoreCase);
    }

    public bool CanUserAccessEmailMessage(Employee user, string accountId, string messageId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user))
            return false;

        return CanUserAccessEmailMessageCore(user.Id, user, user.IsAdmin, accountId, messageId);
    }

    public bool CanUserAccessEmailMessage(string employeeId, string accountId, string messageId, bool isAdmin = false) =>
        CanUserAccessEmailMessageCore(employeeId, suppliedUser: null, isAdmin, accountId, messageId);

    private bool CanUserAccessEmailMessageCore(string employeeId, Employee? suppliedUser, bool isAdminHint, string accountId, string messageId)
    {
        if (IsFamilyEdition() || string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(accountId))
            return false;

        var persistedUser = _employees?.GetById(employeeId);
        if (_employees != null && persistedUser == null && suppliedUser == null)
        {
            return false;
        }

        var effectiveUser = persistedUser ?? suppliedUser;
        if (effectiveUser != null && !FileAccessPermissionHelper.IsReadableUser(effectiveUser))
        {
            return false;
        }

        bool isEffectiveAdmin = effectiveUser?.IsAdmin ?? isAdminHint;
        bool hasEmailUsePermission = effectiveUser != null
            ? FileAccessPermissionHelper.HasAssignedPermission(effectiveUser, AppPermissions.Email.Use)
            : (isAdminHint || _employees == null);

        var account = _accounts?.GetById(accountId);
        var teamIds = GetUserTeamIds(employeeId);

        // Personal (non-server-managed) mailbox privacy: even admins cannot read a personal mailbox unless explicitly shared or owned
        bool hasFullRealInboxAccess;
        if (account != null)
        {
            if (account.IsServerManaged)
            {
                hasFullRealInboxAccess = isEffectiveAdmin || account.CanUserAccess(employeeId, teamIds);
            }
            else
            {
                hasFullRealInboxAccess = account.CanUserAccess(employeeId, teamIds);
            }
        }
        else
        {
            hasFullRealInboxAccess = isEffectiveAdmin || string.Equals(employeeId, accountId, StringComparison.OrdinalIgnoreCase);
        }

        var msg = !string.IsNullOrWhiteSpace(messageId)
            ? (_messages?.GetById(accountId, messageId) ?? _messages?.GetById(messageId))
            : null;

        // Enforce strict per-creator LocalDrafts isolation BEFORE hasFullRealInboxAccess so co-workers and admins
        // on shared or server-managed accounts cannot read or download attachments from another user's unsent draft.
        if (msg != null && IsLocalDraftMessage(msg))
        {
            if (!hasEmailUsePermission || !CanEmployeeAccessLocalDraft(msg, employeeId, account, accountId))
            {
                return false;
            }
        }

        if (hasFullRealInboxAccess && hasEmailUsePermission)
        {
            return true;
        }

        // Check Company Bill Inbox access on the bound server-managed account (never personal mailboxes)
        var billInboxSettings = _companyProfile?.GetAccounting()?.BillInbox;
        if (billInboxSettings != null &&
            !string.IsNullOrWhiteSpace(billInboxSettings.AccountId) &&
            string.Equals(billInboxSettings.AccountId.Trim(), accountId, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(accountId, CompanyBillInboxConstants.SystemEmployeeId, StringComparison.OrdinalIgnoreCase) ||
             account is { IsServerManaged: true }))
        {
            bool hasBillPermission = isEffectiveAdmin || (effectiveUser != null &&
                (FileAccessPermissionHelper.HasAssignedPermission(effectiveUser, AppPermissions.Finance.AccessAccounting) ||
                 FileAccessPermissionHelper.HasAssignedPermission(effectiveUser, AppPermissions.Purchases.ViewBills) ||
                 FileAccessPermissionHelper.HasAssignedPermission(effectiveUser, AppPermissions.Purchases.ManageBills) ||
                 FileAccessPermissionHelper.HasAssignedPermission(effectiveUser, AppPermissions.Purchases.ApproveBills)));

            if (hasBillPermission && (msg == null || !IsLocalDraftMessage(msg)))
            {
                if (string.IsNullOrWhiteSpace(billInboxSettings.InboxId))
                {
                    return true;
                }

                var billVirtualInbox = _inboxes?.GetById(billInboxSettings.InboxId.Trim());
                if (billVirtualInbox is { IsServerManaged: true } && msg != null && DoesMessageMatchInbox(msg, billVirtualInbox))
                {
                    return true;
                }
            }
        }

        if (!hasEmailUsePermission || _inboxes == null)
        {
            return false;
        }

        // Check Virtual Inbox access on this account
        var virtualInboxes = _inboxes.GetAccessibleForUser(employeeId, teamIds)
            .Where(i => IsVirtualInboxBackingAccountAuthorized(i) &&
                        string.Equals(i.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (virtualInboxes.Count == 0 || msg == null)
        {
            return false;
        }

        if (IsLocalDraftMessage(msg))
        {
            return CanEmployeeAccessLocalDraft(msg, employeeId, account, accountId);
        }

        return IsPrimaryRootInboxMessage(msg) &&
               virtualInboxes.Any(inbox => DoesMessageMatchInbox(msg, inbox));
    }

    private void AuthorizeEntityMutationCaller(string? callerEmployeeId, bool isServerManagedEntity, string? ownerEmployeeId)
    {
        if (_employees == null && string.IsNullOrWhiteSpace(callerEmployeeId))
        {
            return;
        }

        var caller = !string.IsNullOrWhiteSpace(callerEmployeeId) ? _employees?.GetById(callerEmployeeId) : null;
        if (caller == null || !caller.IsActive || caller.IsBanned || caller.IsSuspended)
        {
            throw new UnauthorizedAccessException("Active non-suspended account required to modify email settings.");
        }

        if (isServerManagedEntity)
        {
            if (!caller.IsAdmin && !caller.HasPermission(AppPermissions.Admin.ManageSettings))
            {
                throw new UnauthorizedAccessException("Admin or ManageSettings permission is required to modify server-managed email configuration.");
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(ownerEmployeeId) ||
            !string.Equals(caller.Id, ownerEmployeeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Only the owner may modify this personal email entity.");
        }
    }

    public void SetRealInboxArchived(string accountId, bool isArchived, string? callerEmployeeId = null)
    {
        if (string.IsNullOrWhiteSpace(accountId) || _accounts == null) return;
        var account = _accounts.GetById(accountId);
        if (account == null) return;

        AuthorizeEntityMutationCaller(callerEmployeeId, account.IsServerManaged, account.OwnerEmployeeId);

        account.IsArchived = isArchived;
        account.ArchivedAt = isArchived ? DateTime.UtcNow : null;
        _accounts.Save(account);

        if (isArchived)
        {
            _idleService?.StopWatchingAccount(accountId);
        }

        _syncStateService?.NotifyUnreadCountChanged(accountId);
    }

    public void DeleteRealInbox(string accountId, string? callerEmployeeId = null)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;

        var account = _accounts?.GetById(accountId);
        if (account != null)
        {
            AuthorizeEntityMutationCaller(callerEmployeeId, account.IsServerManaged, account.OwnerEmployeeId);
        }
        else if (!string.IsNullOrWhiteSpace(callerEmployeeId))
        {
            AuthorizeEntityMutationCaller(callerEmployeeId, isServerManagedEntity: true, ownerEmployeeId: null);
        }

        _idleService?.StopWatchingAccount(accountId);

        if (_inboxes != null)
        {
            foreach (var inbox in _inboxes.GetByAccount(accountId).ToList())
            {
                DeleteVirtualInboxCore(inbox.Id);
            }
        }

        if (_identities != null)
        {
            var identitiesToDelete = _identities.GetAll()
                .Where(i =>
                    (!string.IsNullOrWhiteSpace(account?.AutoCreatedIdentityId) && string.Equals(i.Id, account.AutoCreatedIdentityId, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(i.SourceAccountId, accountId, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Id)
                .ToList();

            foreach (var identityId in identitiesToDelete)
            {
                DeleteSendingIdentityCore(identityId);
            }
        }

        _folders?.DeleteByAccount(accountId);
        _messages?.DeleteByAccount(accountId);
        _accounts?.Delete(accountId);
        _syncStateService?.RemoveAccount(accountId);
    }

    public void DeleteVirtualInbox(string inboxId, string? callerEmployeeId = null)
    {
        if (string.IsNullOrWhiteSpace(inboxId)) return;

        var inbox = _inboxes?.GetById(inboxId);
        if (inbox != null)
        {
            AuthorizeEntityMutationCaller(callerEmployeeId, inbox.IsServerManaged, inbox.OwnerEmployeeId);
        }
        else if (!string.IsNullOrWhiteSpace(callerEmployeeId))
        {
            AuthorizeEntityMutationCaller(callerEmployeeId, isServerManagedEntity: true, ownerEmployeeId: null);
        }

        DeleteVirtualInboxCore(inboxId);
    }

    private void DeleteVirtualInboxCore(string inboxId)
    {
        var inbox = _inboxes?.GetById(inboxId);

        if (_identities != null)
        {
            var sourcedIdentities = _identities.GetAll()
                .Where(i => i.SourceType == EmailIdentitySourceType.VirtualInbox &&
                            string.Equals(i.SourceInboxId, inboxId, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Id)
                .ToList();

            foreach (var identityId in sourcedIdentities)
            {
                DeleteSendingIdentityCore(identityId);
            }
        }

        _inboxes?.Delete(inboxId);

        if (!string.IsNullOrWhiteSpace(inbox?.AccountId))
        {
            _syncStateService?.NotifyUnreadCountChanged(inbox.AccountId);
        }
    }

    public void DeleteSendingIdentity(string identityId, string? callerEmployeeId = null)
    {
        if (string.IsNullOrWhiteSpace(identityId)) return;

        var identity = _identities?.GetById(identityId);
        if (identity != null)
        {
            AuthorizeEntityMutationCaller(callerEmployeeId, identity.IsServerManaged, identity.OwnerEmployeeId);
        }
        else if (!string.IsNullOrWhiteSpace(callerEmployeeId))
        {
            AuthorizeEntityMutationCaller(callerEmployeeId, isServerManagedEntity: true, ownerEmployeeId: null);
        }

        DeleteSendingIdentityCore(identityId);
    }

    private static void AddDomainMatchedPersonalIdentities(
        Dictionary<string, EmailSendingIdentity> result,
        List<EmailSendingIdentity> userAccessibleIdentities,
        IEnumerable<string> mailboxDomains,
        IEnumerable<string>? replyReceivingAddresses)
    {
        var allowedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dom in mailboxDomains)
        {
            if (!string.IsNullOrWhiteSpace(dom)) allowedDomains.Add(dom);
        }

        if (replyReceivingAddresses != null)
        {
            foreach (var addr in replyReceivingAddresses)
            {
                var dom = ExtractDomain(addr);
                if (!string.IsNullOrEmpty(dom)) allowedDomains.Add(dom);
            }
        }

        foreach (var personalId in userAccessibleIdentities)
        {
            if (allowedDomains.Count == 0 || allowedDomains.Contains(personalId.EffectiveDomain))
            {
                result[personalId.Id] = personalId;
            }
        }
    }

    private static bool UnlinkIdentityReferences(
        string identityId,
        List<string>? linkedIdentityIds,
        ref string? defaultIdentityId)
    {
        bool modified = false;
        if (linkedIdentityIds != null && linkedIdentityIds.RemoveAll(id => string.Equals(id, identityId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            modified = true;
        }
        if (string.Equals(defaultIdentityId, identityId, StringComparison.OrdinalIgnoreCase))
        {
            defaultIdentityId = linkedIdentityIds?.FirstOrDefault();
            modified = true;
        }
        return modified;
    }

    private void DeleteSendingIdentityCore(string identityId)
    {
        if (_accounts != null)
        {
            foreach (var acc in _accounts.GetAll().ToList())
            {
                var defaultId = acc.DefaultIdentityId;
                bool modified = UnlinkIdentityReferences(identityId, acc.LinkedIdentityIds, ref defaultId);
                acc.DefaultIdentityId = defaultId;
                if (string.Equals(acc.AutoCreatedIdentityId, identityId, StringComparison.OrdinalIgnoreCase))
                {
                    acc.AutoCreatedIdentityId = null;
                    modified = true;
                }
                if (modified)
                {
                    _accounts.Save(acc);
                }
            }
        }

        if (_inboxes != null)
        {
            foreach (var inbox in _inboxes.GetAll().ToList())
            {
                var defaultId = inbox.DefaultIdentityId;
                if (UnlinkIdentityReferences(identityId, inbox.LinkedIdentityIds, ref defaultId))
                {
                    inbox.DefaultIdentityId = defaultId;
                    _inboxes.Save(inbox);
                }
            }
        }

        _identities?.Delete(identityId);
    }

    private static (string LocalUser, string Domain) SplitEmailAddress(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return (string.Empty, string.Empty);
        var trimmed = email.Trim().ToLowerInvariant();
        var atIdx = trimmed.IndexOf('@');
        if (atIdx <= 0 || atIdx >= trimmed.Length - 1)
            return (trimmed, string.Empty);

        return (trimmed[..atIdx].Trim(), trimmed[(atIdx + 1)..].Trim());
    }

    private static string ExtractDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        var trimmed = email.Trim().TrimStart('*');
        var atIdx = trimmed.IndexOf('@');
        return atIdx >= 0 && atIdx < trimmed.Length - 1
            ? trimmed[(atIdx + 1)..].Trim().ToLowerInvariant()
            : string.Empty;
    }
}
