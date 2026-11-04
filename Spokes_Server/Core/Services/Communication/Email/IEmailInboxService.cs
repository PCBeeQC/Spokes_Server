using System;
using System.Collections.Generic;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailInboxService
{
    HashSet<string> GetUserTeamIds(string employeeId);
    List<EmailInbox> GetAccessibleInboxes(string employeeId);
    List<EmailInbox> GetAccessibleInboxes(Employee employee);
    List<EmailAccount> GetAccessibleRealInboxes(string employeeId, bool includeArchived = false);
    List<EmailAccount> GetAccessibleRealInboxes(Employee employee, bool includeArchived = false);
    List<EmailAccount> GetAccessibleArchivedRealInboxes(string employeeId);
    List<EmailAccount> GetAccessibleArchivedRealInboxes(Employee employee);
    List<EmailAccount> GetAccessibleAccounts(string employeeId, bool includeArchived = false);
    List<EmailAccount> GetAccessibleAccounts(Employee employee, bool includeArchived = false);
    void SetRealInboxArchived(string accountId, bool isArchived);
    void DeleteRealInbox(string accountId);
    void DeleteVirtualInbox(string inboxId);
    void DeleteSendingIdentity(string identityId);
    Func<MessageMetadata, bool>? GetMetadataFilterForInbox(EmailInbox inbox);
    bool DoesMessageMatchInbox(EmailMessage message, EmailInbox inbox);
    List<string> GetUnmatchedAddressesForMessage(EmailMessage message);
    List<string> GetDetectedAddressesForAccount(string accountId);
    List<string> GetUnmatchedAddressesForAccount(string accountId);
    int GetUnreadCountForInbox(EmailInbox inbox, string? folderPath = null);
    int GetUnreadCountForRealInbox(string accountId, string? folderPath = null);
    int GetTotalUnreadCountForUser(string employeeId);
    int GetUnseenUnreadCountForUser(string employeeId);
    void MarkEmailPageVisited(string employeeId);
    List<EmailMessage> GetInboxMessages(EmailInbox inbox, int count = 50, DateTime? beforeDateUtc = null);
    List<EmailMessage> GetRealInboxMessages(string accountId, int count = 50, DateTime? beforeDateUtc = null);
    List<EmailMessage> GetAllInboxesMessages(Employee employee, int count = 50, DateTime? beforeDateUtc = null);
    EmailSendingIdentity EnsureRealInboxSendingIdentity(EmailAccount account, string? displayName = null);
    EmailSendingIdentity EnsureVirtualInboxSendingIdentity(EmailInbox inbox, string outgoingServerConfigId, string? displayName = null);
    List<EmailSendingIdentity> GetPermittedIdentities(string employeeId, EmailInbox? inbox = null, IEnumerable<string>? replyReceivingAddresses = null, EmailAccount? realInbox = null);
    List<EmailSendingIdentity> GetPermittedIdentities(Employee employee, EmailInbox? inbox = null, IEnumerable<string>? replyReceivingAddresses = null, EmailAccount? realInbox = null);
    bool IsIdentityPermittedForUser(string employeeId, string identityId, string? inboxId = null);
    bool IsIdentityPermittedForUser(string employeeId, EmailSendingIdentity identity, string? inboxId = null);
    EmailSendingIdentity? SelectDefaultIdentity(IReadOnlyList<EmailSendingIdentity> permittedIdentities, string employeeId, EmailInbox? inbox = null, EmailMessage? replyToMessage = null, EmailAccount? realInbox = null);
    EmailSendingIdentity? SelectDefaultIdentity(Employee employee, EmailInbox? inbox = null, IEnumerable<string>? replyReceivingAddresses = null, EmailAccount? realInbox = null);
    List<string> GetAuthorizedEmployeeIdsForMessage(EmailMessage message);
    bool CanUserAccessEmailMessage(string employeeId, string accountId, string messageId, bool isAdmin = false);
    bool CanUserAccessEmailMessage(Employee user, string accountId, string messageId);
}
