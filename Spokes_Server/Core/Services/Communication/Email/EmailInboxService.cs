using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

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

    public HashSet<string> GetUserTeamIds(string employeeId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(employeeId)) return result;

        var emp = _employees?.GetById(employeeId);
        if (emp != null && !string.IsNullOrWhiteSpace(emp.TeamId))
        {
            result.Add(emp.TeamId);
        }

        if (_teams != null)
        {
            foreach (var team in _teams.GetAll())
            {
                if (string.Equals(team.LeaderId, employeeId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(team.Id);
                }
            }
        }

        return result;
    }

    public List<EmailInbox> GetAccessibleInboxes(Employee employee) =>
        employee == null ? [] : GetAccessibleInboxes(employee.Id);

    public List<EmailInbox> GetAccessibleInboxes(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _inboxes == null) return [];
        var teamIds = GetUserTeamIds(employeeId);
        var archivedAccountIds = _accounts != null
            ? _accounts.GetAll().Where(a => a.IsArchived).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return _inboxes.GetAccessibleForUser(employeeId, teamIds)
            .Where(i => !archivedAccountIds.Contains(i.AccountId))
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Name)
            .ToList();
    }

    public List<EmailAccount> GetAccessibleRealInboxes(Employee employee, bool includeArchived = false) =>
        employee == null ? [] : GetAccessibleRealInboxes(employee.Id, includeArchived);

    public List<EmailAccount> GetAccessibleRealInboxes(string employeeId, bool includeArchived = false)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _accounts == null) return [];
        var teamIds = GetUserTeamIds(employeeId);

        return _accounts.GetAccessibleForUser(employeeId, teamIds, includeArchived);
    }

    public List<EmailAccount> GetAccessibleArchivedRealInboxes(Employee employee) =>
        employee == null ? [] : GetAccessibleArchivedRealInboxes(employee.Id);

    public List<EmailAccount> GetAccessibleArchivedRealInboxes(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _accounts == null) return [];
        var teamIds = GetUserTeamIds(employeeId);

        return _accounts.GetArchivedAccessibleForUser(employeeId, teamIds);
    }

    public List<EmailAccount> GetAccessibleAccounts(Employee employee, bool includeArchived = false) =>
        employee == null ? [] : GetAccessibleAccounts(employee.Id, includeArchived);

    public List<EmailAccount> GetAccessibleAccounts(string employeeId, bool includeArchived = false)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || _accounts == null) return [];

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
        var folders = _folders.GetByAccount(accountId);
        var inboxFolder = folders.FirstOrDefault(f => f.IsInbox)
                          ?? folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
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

        var folders = _folders.GetByAccount(inbox.AccountId);
        var inboxFolder = folders.FirstOrDefault(f => f.IsInbox)
                          ?? folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
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

        var folders = _folders.GetByAccount(accountId);
        var inboxFolder = folders.FirstOrDefault(f => f.IsInbox)
                          ?? folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
        if (inboxFolder == null) return 0;

        return _messages.GetUnreadCount(accountId, inboxFolder.Path);
    }

    public int GetTotalUnreadCountForUser(string employeeId) =>
        GetUnreadCountForUserCore(employeeId, sinceUtc: null);

    public int GetUnseenUnreadCountForUser(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return 0;
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

    private int GetUnreadCountForUserCore(string employeeId, DateTimeOffset? sinceUtc)
    {
        var realInboxes = GetAccessibleRealInboxes(employeeId);
        var virtualInboxes = GetAccessibleInboxes(employeeId);
        if (realInboxes.Count == 0 && virtualInboxes.Count == 0) return 0;

        var unreadMessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in realInboxes)
        {
            var folders = _folders.GetByAccount(account.Id);
            var inboxFolder = folders.FirstOrDefault(f => f.IsInbox)
                              ?? folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
            if (inboxFolder == null) continue;

            foreach (var meta in _messages.GetFolderMetadata(account.Id, inboxFolder.Path))
            {
                if (!meta.IsRead && (!sinceUtc.HasValue || meta.Date > sinceUtc.Value))
                {
                    unreadMessageIds.Add(meta.Id);
                }
            }
        }

        foreach (var inbox in virtualInboxes)
        {
            var folders = _folders.GetByAccount(inbox.AccountId);
            var inboxFolder = folders.FirstOrDefault(f => f.IsInbox)
                              ?? folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
            if (inboxFolder == null) continue;

            var filter = GetMetadataFilterForInbox(inbox);
            foreach (var meta in _messages.GetFolderMetadata(inbox.AccountId, inboxFolder.Path))
            {
                if (!meta.IsRead && (!sinceUtc.HasValue || meta.Date > sinceUtc.Value) && (filter == null || filter(meta)))
                {
                    unreadMessageIds.Add(meta.Id);
                }
            }
        }

        return unreadMessageIds.Count;
    }

    private DateTimeOffset? GetLatestUnreadInboxMessageDate(string employeeId)
    {
        var realInboxes = GetAccessibleRealInboxes(employeeId);
        var virtualInboxes = GetAccessibleInboxes(employeeId);
        DateTimeOffset? latest = null;

        foreach (var account in realInboxes)
        {
            var folders = _folders.GetByAccount(account.Id);
            var inboxFolder = folders.FirstOrDefault(f => f.IsInbox)
                              ?? folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
            if (inboxFolder == null) continue;

            foreach (var meta in _messages.GetFolderMetadata(account.Id, inboxFolder.Path))
            {
                if (!meta.IsRead && (!latest.HasValue || meta.Date > latest.Value))
                {
                    latest = meta.Date;
                }
            }
        }

        foreach (var inbox in virtualInboxes)
        {
            var folders = _folders.GetByAccount(inbox.AccountId);
            var inboxFolder = folders.FirstOrDefault(f => f.IsInbox)
                              ?? folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
            if (inboxFolder == null) continue;

            var filter = GetMetadataFilterForInbox(inbox);
            foreach (var meta in _messages.GetFolderMetadata(inbox.AccountId, inboxFolder.Path))
            {
                if (!meta.IsRead && (filter == null || filter(meta)) && (!latest.HasValue || meta.Date > latest.Value))
                {
                    latest = meta.Date;
                }
            }
        }

        return latest;
    }

    public List<EmailMessage> GetInboxMessages(EmailInbox inbox, int count = 50, DateTime? beforeDateUtc = null)
    {
        var folders = _folders.GetByAccount(inbox.AccountId);
        var inboxFolder = folders.FirstOrDefault(f => f.IsInbox)
                          ?? folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
        if (inboxFolder == null) return [];

        var filter = GetMetadataFilterForInbox(inbox);
        return beforeDateUtc.HasValue
            ? _messages.GetByFolderBeforeDate(inbox.AccountId, inboxFolder.Path, beforeDateUtc.Value, count, filter)
            : _messages.GetByFolderRecent(inbox.AccountId, inboxFolder.Path, count, filter);
    }

    public List<EmailMessage> GetRealInboxMessages(string accountId, int count = 50, DateTime? beforeDateUtc = null)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return [];
        var folders = _folders.GetByAccount(accountId);
        var inboxFolder = folders.FirstOrDefault(f => f.IsInbox)
                          ?? folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
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

    public EmailSendingIdentity EnsureVirtualInboxSendingIdentity(EmailInbox inbox, string outgoingServerConfigId, string? displayName = null)
    {
        if (inbox == null) throw new ArgumentNullException(nameof(inbox));

        var primaryAddr = inbox.PrimaryAddress ?? string.Empty;
        var (localUser, domain) = SplitEmailAddress(primaryAddr);

        var settings = _companyProfile?.Get()?.EmailSettings;
        var sendingServer = settings?.SendingServers?.FirstOrDefault(s => string.Equals(s.Id, outgoingServerConfigId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(domain) && sendingServer != null && !string.IsNullOrWhiteSpace(sendingServer.Domain))
        {
            domain = sendingServer.Domain.Trim().TrimStart('@').ToLowerInvariant();
        }
        if (string.IsNullOrWhiteSpace(localUser) && !string.IsNullOrWhiteSpace(inbox.Name))
        {
            localUser = inbox.Name.Trim().ToLowerInvariant().Replace(' ', '.');
        }

        var existing = _identities.GetAll().FirstOrDefault(i =>
            i.SourceType == EmailIdentitySourceType.VirtualInbox &&
            string.Equals(i.SourceInboxId, inbox.Id, StringComparison.OrdinalIgnoreCase));

        var resolvedDisplayName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : (!string.IsNullOrWhiteSpace(inbox.Name) ? inbox.Name.Trim() : $"{localUser}@{domain}");

        if (existing == null)
        {
            existing = new EmailSendingIdentity
            {
                OwnerEmployeeId = inbox.OwnerEmployeeId,
                IsServerManaged = inbox.IsServerManaged,
                SourceType = EmailIdentitySourceType.VirtualInbox,
                SourceInboxId = inbox.Id,
                SourceAccountId = inbox.AccountId,
                SendingServerConfigId = outgoingServerConfigId,
                DisplayName = resolvedDisplayName,
                Username = localUser,
                Domain = domain,
                AllowedUserIds = new List<string>(inbox.AllowedUserIds ?? []),
                AllowedTeamIds = new List<string>(inbox.AllowedTeamIds ?? [])
            };
        }
        else
        {
            existing.OwnerEmployeeId = inbox.OwnerEmployeeId;
            existing.IsServerManaged = inbox.IsServerManaged;
            existing.SourceType = EmailIdentitySourceType.VirtualInbox;
            existing.SourceInboxId = inbox.Id;
            existing.SourceAccountId = inbox.AccountId;
            existing.SendingServerConfigId = outgoingServerConfigId;
            existing.DisplayName = resolvedDisplayName;
            if (!string.IsNullOrWhiteSpace(localUser)) existing.Username = localUser;
            if (!string.IsNullOrWhiteSpace(domain)) existing.Domain = domain;
            existing.AllowedUserIds = new List<string>(inbox.AllowedUserIds ?? []);
            existing.AllowedTeamIds = new List<string>(inbox.AllowedTeamIds ?? []);
        }

        _identities.Save(existing);

        inbox.LinkedIdentityIds ??= [];
        if (!inbox.LinkedIdentityIds.Contains(existing.Id, StringComparer.OrdinalIgnoreCase))
        {
            inbox.LinkedIdentityIds.Add(existing.Id);
        }
        if (string.IsNullOrWhiteSpace(inbox.DefaultIdentityId))
        {
            inbox.DefaultIdentityId = existing.Id;
        }
        _inboxes.Save(inbox);

        return existing;
    }

    public List<EmailSendingIdentity> GetPermittedIdentities(Employee employee, EmailInbox? inbox = null, IEnumerable<string>? replyReceivingAddresses = null, EmailAccount? realInbox = null) =>
        employee == null ? [] : GetPermittedIdentities(employee.Id, inbox, replyReceivingAddresses, realInbox);

    public List<EmailSendingIdentity> GetPermittedIdentities(string employeeId, EmailInbox? inbox = null, IEnumerable<string>? replyReceivingAddresses = null, EmailAccount? realInbox = null)
    {
        var teamIds = GetUserTeamIds(employeeId);
        var archivedAccountIds = _accounts != null
            ? _accounts.GetAll().Where(a => a.IsArchived).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allIdentities = _identities.GetAll()
            .Where(i => string.IsNullOrWhiteSpace(i.SourceAccountId) || !archivedAccountIds.Contains(i.SourceAccountId))
            .ToList();
        var userAccessibleIdentities = _identities.GetAccessibleForUser(employeeId, teamIds)
            .Where(i => string.IsNullOrWhiteSpace(i.SourceAccountId) || !archivedAccountIds.Contains(i.SourceAccountId))
            .ToList();

        if (inbox == null && realInbox == null)
        {
            var accessibleInboxes = GetAccessibleInboxes(employeeId)
                .Where(i => string.IsNullOrWhiteSpace(i.AccountId) || !archivedAccountIds.Contains(i.AccountId))
                .ToList();
            var accessibleRealInboxes = GetAccessibleRealInboxes(employeeId);
            var accessibleInboxIds = accessibleInboxes.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var accessibleRealInboxIds = accessibleRealInboxes.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var linkedIds = accessibleInboxes
                .Where(i => i.LinkedIdentityIds != null)
                .SelectMany(i => i.LinkedIdentityIds)
                .Concat(accessibleInboxes.Where(i => !string.IsNullOrWhiteSpace(i.DefaultIdentityId)).Select(i => i.DefaultIdentityId!))
                .Concat(accessibleRealInboxes.Where(a => a.LinkedIdentityIds != null).SelectMany(a => a.LinkedIdentityIds))
                .Concat(accessibleRealInboxes.Where(a => !string.IsNullOrWhiteSpace(a.DefaultIdentityId)).Select(a => a.DefaultIdentityId!))
                .Concat(accessibleRealInboxes.Where(a => !string.IsNullOrWhiteSpace(a.AutoCreatedIdentityId)).Select(a => a.AutoCreatedIdentityId!))
                .Concat(allIdentities
                    .Where(i => i.SourceType == EmailIdentitySourceType.VirtualInbox &&
                                !string.IsNullOrWhiteSpace(i.SourceInboxId) &&
                                accessibleInboxIds.Contains(i.SourceInboxId))
                    .Select(i => i.Id))
                .Concat(allIdentities
                    .Where(i => i.SourceType == EmailIdentitySourceType.RealInbox &&
                                !string.IsNullOrWhiteSpace(i.SourceAccountId) &&
                                accessibleRealInboxIds.Contains(i.SourceAccountId))
                    .Select(i => i.Id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return allIdentities
                .Where(id => linkedIds.Contains(id.Id) || id.CanUserAccess(employeeId, teamIds))
                .OrderBy(id => id.DisplayName)
                .ThenBy(id => id.EmailAddress)
                .ToList();
        }

        var result = new Dictionary<string, EmailSendingIdentity>(StringComparer.OrdinalIgnoreCase);

        // 1. Process Virtual Inbox if specified and accessible to the user (takes precedence for virtual inboxes)
        if (inbox != null && inbox.CanUserAccess(employeeId, teamIds))
        {
            if (!string.IsNullOrWhiteSpace(inbox.DefaultIdentityId))
            {
                var def = allIdentities.FirstOrDefault(i => string.Equals(i.Id, inbox.DefaultIdentityId, StringComparison.OrdinalIgnoreCase));
                if (def != null) result[def.Id] = def;
            }

            if (inbox.LinkedIdentityIds != null)
            {
                foreach (var linkedId in inbox.LinkedIdentityIds)
                {
                    var found = allIdentities.FirstOrDefault(i => string.Equals(i.Id, linkedId, StringComparison.OrdinalIgnoreCase));
                    if (found != null) result[found.Id] = found;
                }
            }

            var inboxSourceIdentities = allIdentities.Where(i =>
                i.SourceType == EmailIdentitySourceType.VirtualInbox &&
                string.Equals(i.SourceInboxId, inbox.Id, StringComparison.OrdinalIgnoreCase));
            foreach (var sid in inboxSourceIdentities)
            {
                result[sid.Id] = sid;
            }

            if (inbox.AllowDomainMatchedPersonalIdentity)
            {
                var allowedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (inbox.TargetAddresses != null)
                {
                    foreach (var addr in inbox.TargetAddresses)
                    {
                        var dom = ExtractDomain(addr);
                        if (!string.IsNullOrEmpty(dom)) allowedDomains.Add(dom);
                    }
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
        }

        // 2. Process Real Inbox if specified and accessible to the user
        if (realInbox != null)
        {
            if (realInbox.CanUserAccess(employeeId, teamIds))
            {
                if (!string.IsNullOrWhiteSpace(realInbox.DefaultIdentityId))
                {
                    var def = allIdentities.FirstOrDefault(i => string.Equals(i.Id, realInbox.DefaultIdentityId, StringComparison.OrdinalIgnoreCase));
                    if (def != null) result[def.Id] = def;
                }

                if (!string.IsNullOrWhiteSpace(realInbox.AutoCreatedIdentityId))
                {
                    var autoId = allIdentities.FirstOrDefault(i => string.Equals(i.Id, realInbox.AutoCreatedIdentityId, StringComparison.OrdinalIgnoreCase));
                    if (autoId != null) result[autoId.Id] = autoId;
                }

                if (realInbox.LinkedIdentityIds != null)
                {
                    foreach (var linkedId in realInbox.LinkedIdentityIds)
                    {
                        var found = allIdentities.FirstOrDefault(i => string.Equals(i.Id, linkedId, StringComparison.OrdinalIgnoreCase));
                        if (found != null) result[found.Id] = found;
                    }
                }

                if (realInbox.AllowDomainMatchedPersonalIdentity && inbox == null)
                {
                    var allowedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(realInbox.PrimaryDomain))
                    {
                        allowedDomains.Add(realInbox.PrimaryDomain);
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

        if (!string.IsNullOrWhiteSpace(inboxId))
        {
            var inbox = _inboxes?.GetById(inboxId);
            if (inbox == null || !inbox.CanUserAccess(employeeId, GetUserTeamIds(employeeId)))
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
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var account = _accounts?.GetById(message.AccountId);
        var allEmployees = (_employees?.GetAll() ?? []).Where(e => e.IsActive && !e.IsSuspended && !e.IsBanned).ToList();

        if (account != null)
        {
            if (!account.IsServerManaged && !string.IsNullOrWhiteSpace(account.OwnerEmployeeId))
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

        var accountInboxes = _inboxes?.GetByAccount(message.AccountId) ?? [];

        foreach (var inbox in accountInboxes)
        {
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

    private static bool CanEmployeeAccessLocalDraft(EmailMessage draft, string employeeId, EmailAccount? account, string accountId)
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
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
            return false;

        return CanUserAccessEmailMessage(user.Id, accountId, messageId, user.IsAdmin);
    }

    public bool CanUserAccessEmailMessage(string employeeId, string accountId, string messageId, bool isAdmin = false)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(accountId))
            return false;

        var account = _accounts?.GetById(accountId);
        var teamIds = GetUserTeamIds(employeeId);

        // Personal (non-server-managed) mailbox privacy: even admins cannot read a personal mailbox unless explicitly shared or owned
        bool hasFullRealInboxAccess;
        if (account != null)
        {
            if (account.IsServerManaged)
            {
                hasFullRealInboxAccess = isAdmin || account.CanUserAccess(employeeId, teamIds);
            }
            else
            {
                hasFullRealInboxAccess = account.CanUserAccess(employeeId, teamIds);
            }
        }
        else
        {
            hasFullRealInboxAccess = isAdmin || string.Equals(employeeId, accountId, StringComparison.OrdinalIgnoreCase);
        }

        var msg = !string.IsNullOrWhiteSpace(messageId)
            ? (_messages?.GetById(accountId, messageId) ?? _messages?.GetById(messageId))
            : null;

        // Enforce strict per-creator LocalDrafts isolation BEFORE hasFullRealInboxAccess so co-workers and admins
        // on shared or server-managed accounts cannot read or download attachments from another user's unsent draft.
        if (msg != null && IsLocalDraftMessage(msg))
        {
            if (!CanEmployeeAccessLocalDraft(msg, employeeId, account, accountId))
            {
                return false;
            }
        }

        if (hasFullRealInboxAccess)
        {
            return true;
        }

        // Check Virtual Inbox access on this account
        var virtualInboxes = GetAccessibleInboxes(employeeId)
            .Where(i => string.Equals(i.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (virtualInboxes.Count == 0)
        {
            return false;
        }

        if (msg == null)
        {
            return false;
        }

        if (IsLocalDraftMessage(msg))
        {
            return CanEmployeeAccessLocalDraft(msg, employeeId, account, accountId);
        }

        return virtualInboxes.Any(inbox => DoesMessageMatchInbox(msg, inbox));
    }

    public void SetRealInboxArchived(string accountId, bool isArchived)
    {
        if (string.IsNullOrWhiteSpace(accountId) || _accounts == null) return;
        var account = _accounts.GetById(accountId);
        if (account == null) return;

        account.IsArchived = isArchived;
        account.ArchivedAt = isArchived ? DateTime.UtcNow : null;
        _accounts.Save(account);

        if (isArchived)
        {
            _idleService?.StopWatchingAccount(accountId);
        }

        _syncStateService?.NotifyUnreadCountChanged(accountId);
    }

    public void DeleteRealInbox(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;

        _idleService?.StopWatchingAccount(accountId);

        var account = _accounts?.GetById(accountId);

        if (_inboxes != null)
        {
            foreach (var inbox in _inboxes.GetByAccount(accountId).ToList())
            {
                DeleteVirtualInbox(inbox.Id);
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
                DeleteSendingIdentity(identityId);
            }
        }

        _folders?.DeleteByAccount(accountId);
        _messages?.DeleteByAccount(accountId);
        _accounts?.Delete(accountId);
        _syncStateService?.RemoveAccount(accountId);
    }

    public void DeleteVirtualInbox(string inboxId)
    {
        if (string.IsNullOrWhiteSpace(inboxId)) return;

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
                DeleteSendingIdentity(identityId);
            }
        }

        _inboxes?.Delete(inboxId);

        if (!string.IsNullOrWhiteSpace(inbox?.AccountId))
        {
            _syncStateService?.NotifyUnreadCountChanged(inbox.AccountId);
        }
    }

    public void DeleteSendingIdentity(string identityId)
    {
        if (string.IsNullOrWhiteSpace(identityId)) return;

        if (_accounts != null)
        {
            foreach (var acc in _accounts.GetAll().ToList())
            {
                bool modified = false;
                if (acc.LinkedIdentityIds != null && acc.LinkedIdentityIds.RemoveAll(id => string.Equals(id, identityId, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    modified = true;
                }
                if (string.Equals(acc.DefaultIdentityId, identityId, StringComparison.OrdinalIgnoreCase))
                {
                    acc.DefaultIdentityId = acc.LinkedIdentityIds?.FirstOrDefault();
                    modified = true;
                }
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
                bool modified = false;
                if (inbox.LinkedIdentityIds != null && inbox.LinkedIdentityIds.RemoveAll(id => string.Equals(id, identityId, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    modified = true;
                }
                if (string.Equals(inbox.DefaultIdentityId, identityId, StringComparison.OrdinalIgnoreCase))
                {
                    inbox.DefaultIdentityId = inbox.LinkedIdentityIds?.FirstOrDefault();
                    modified = true;
                }
                if (modified)
                {
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
