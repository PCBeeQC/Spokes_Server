using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailFolderService : IEmailFolderService
{
    private readonly EmailAccountRepository? _accounts;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly EmailSyncStateService? _syncStateService;
    private readonly ILogger<EmailFolderService>? _logger;
    private readonly IImapConnectionFactory _imapFactory;
    private readonly EmployeeRepository? _employees;
    private readonly TeamRepository? _teams;

    public EmailFolderService(
        CompanyProfileRepository companyProfile,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        EncryptionService encryptionService,
        EmailAccountRepository? accounts = null,
        ILogger<EmailFolderService>? logger = null,
        IImapConnectionFactory? imapFactory = null,
        EmailSyncStateService? syncStateService = null,
        EmployeeRepository? employees = null,
        TeamRepository? teams = null)
    {
        _accounts = accounts;
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _syncStateService = syncStateService;
        _logger = logger;
        _employees = employees;
        _teams = teams;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, encryptionService, accounts, null, employees);
    }

    private HashSet<string> GetUserTeamIds(Employee caller) =>
        TeamRepository.GetUserTeamIds(_teams, caller.Id, caller);

    private SpokesResult? ValidateFolderMutationCaller(string accountId, string? callerEmployeeId)
    {
        if (string.Equals(_companyProfile?.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
        {
            return SpokesResult.Failure("Email is not available in Family edition.", "ACCESS_DENIED");
        }

        if (_employees == null && string.IsNullOrWhiteSpace(callerEmployeeId))
        {
            return null;
        }

        var caller = !string.IsNullOrWhiteSpace(callerEmployeeId) ? _employees?.GetById(callerEmployeeId) : null;
        if (caller == null || !caller.IsActive || caller.IsBanned || caller.IsSuspended ||
            !caller.HasPermission(AppPermissions.Email.Use))
        {
            return SpokesResult.Failure("Suspended or unauthorized users cannot modify email folders.", "ACCESS_DENIED");
        }

        if (_accounts != null)
        {
            var acc = _accounts.GetById(accountId);
            if (acc == null || acc.IsArchived)
            {
                return SpokesResult.Failure("You do not have access to modify folders for this mailbox.", "ACCESS_DENIED");
            }

            bool authorized = (acc.IsServerManaged && (caller.IsEffectiveAdmin || caller.HasPermission(AppPermissions.Admin.ManageSettings))) ||
                              acc.CanUserAccess(caller.Id, GetUserTeamIds(caller));
            if (!authorized)
            {
                return SpokesResult.Failure("You are not authorized to modify folders for this mailbox.", "ACCESS_DENIED");
            }
        }

        return null;
    }

    public async Task<SpokesResult<EmailFolder>> CreateFolderAsync(string accountId, string parentFolderPath, string newFolderName, string? callerEmployeeId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult<EmailFolder>.Failure("Account ID is required.");

        if (string.IsNullOrWhiteSpace(newFolderName))
            return SpokesResult<EmailFolder>.Failure("Folder name cannot be empty.");

        if (ValidateFolderMutationCaller(accountId, callerEmployeeId) is { } authFailure)
            return SpokesResult<EmailFolder>.Failure(authFailure.ErrorMessage ?? "Access denied.", authFailure.Code ?? "ACCESS_DENIED");

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(accountId, cancellationToken);
            if (client == null)
            {
                return SpokesResult<EmailFolder>.Failure("Unable to connect or authenticate to the email server. Please check your credentials.");
            }

            var (parentFolder, parentError) = await ResolveParentFolderAsync(client, parentFolderPath, cancellationToken);
            if (parentFolder == null)
            {
                _logger?.LogWarning("Failed to create folder '{FolderName}' for Account {AccountId}: parent folder was not found on IMAP server.", newFolderName, accountId);
                return SpokesResult<EmailFolder>.Failure(parentError ?? $"Parent folder '{parentFolderPath}' was not found on the email server.");
            }

            var newFolder = await parentFolder.CreateAsync(newFolderName, true, cancellationToken);
            if (newFolder == null)
            {
                _logger?.LogWarning("Failed to create folder '{FolderName}' for Account {AccountId}: IMAP folder creation returned null.", newFolderName, accountId);
                return SpokesResult<EmailFolder>.Failure($"Email server failed to create folder '{newFolderName}'.");
            }

            var existingFolders = _emailFolders.GetByAccount(accountId);
            var localFolder = existingFolders.FirstOrDefault(f => f.Path == newFolder.FullName);
            if (localFolder == null)
            {
                var createdParentPath = GetParentPath(newFolder);
                var isRootInbox = string.IsNullOrEmpty(createdParentPath) &&
                                  (newFolder.Attributes.HasFlag(FolderAttributes.Inbox) ||
                                   newFolder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
                localFolder = new EmailFolder
                {
                    AccountId = accountId,
                    Name = newFolder.Name,
                    Path = newFolder.FullName,
                    ParentPath = createdParentPath,
                    Delimiter = newFolder.DirectorySeparator.ToString(),
                    IsInbox = isRootInbox,
                    IsSent = newFolder.Attributes.HasFlag(FolderAttributes.Sent),
                    IsTrash = newFolder.Attributes.HasFlag(FolderAttributes.Trash),
                    IsDrafts = newFolder.Attributes.HasFlag(FolderAttributes.Drafts),
                    IsArchive = newFolder.Attributes.HasFlag(FolderAttributes.Archive),
                    IsJunk = newFolder.Attributes.HasFlag(FolderAttributes.Junk)
                };
                _emailFolders.Save(localFolder);
            }

            await client.DisconnectAsync(true, cancellationToken);

            return SpokesResult<EmailFolder>.Success(localFolder);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create folder {FolderName}", newFolderName);
            return SpokesResult<EmailFolder>.Failure(ex.Message);
        }
    }

    public Task<SpokesResult> DeleteFolderAsync(string accountId, string folderPath, string? callerEmployeeId = null, CancellationToken cancellationToken = default) =>
        ExecuteFolderMutationAsync(
            accountId,
            folderPath,
            callerEmployeeId,
            "delete",
            async (client, folder, ct) =>
            {
                await folder.DeleteAsync(ct);
                await client.DisconnectAsync(true, ct);

                var folders = _emailFolders.GetByAccount(accountId);
                var local = folders.FirstOrDefault(f => f.Path == folderPath);
                if (local != null) _emailFolders.Delete(local.Id);

                _syncStateService?.NotifyUnreadCountChanged(accountId);
                return SpokesResult.Success();
            },
            cancellationToken);

    public Task<SpokesResult> RenameFolderAsync(string accountId, string folderPath, string newName, string? callerEmployeeId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return Task.FromResult(SpokesResult.Failure("New folder name cannot be empty."));

        return ExecuteFolderMutationAsync(
            accountId,
            folderPath,
            callerEmployeeId,
            "rename",
            async (client, folder, ct) =>
            {
                var parentPath = GetParentPath(folder);
                var (parentFolder, _) = await ResolveParentFolderAsync(client, parentPath, ct);
                if (parentFolder == null)
                {
                    _logger?.LogWarning("Failed to rename folder '{FolderPath}' for Account {AccountId}: parent folder not found on IMAP server.", folderPath, accountId);
                    return SpokesResult.Failure("Parent folder not found on email server.");
                }

                await folder.RenameAsync(parentFolder, newName, ct);
                var renamedFolder = await parentFolder.GetSubfolderAsync(newName, ct);
                if (renamedFolder == null)
                {
                    _logger?.LogWarning("Failed to retrieve renamed folder '{NewName}' for Account {AccountId}.", newName, accountId);
                    return SpokesResult.Failure($"Failed to retrieve renamed folder '{newName}' from email server.");
                }

                await client.DisconnectAsync(true, ct);

                var folders = _emailFolders.GetByAccount(accountId);
                var local = folders.FirstOrDefault(f => f.Path == folderPath);
                if (local != null)
                {
                    var oldPath = local.Path;
                    local.Name = renamedFolder.Name;
                    local.Path = renamedFolder.FullName;
                    local.ParentPath = GetParentPath(renamedFolder);
                    local.Delimiter = renamedFolder.DirectorySeparator.ToString();

                    _emailFolders.Delete(local.Id);
                    _emailFolders.Save(local);

                    _emailMessages.RenameFolder(accountId, oldPath, renamedFolder.FullName);
                    _syncStateService?.NotifyUnreadCountChanged(accountId);
                }

                return SpokesResult.Success();
            },
            cancellationToken);
    }

    private async Task<SpokesResult> ExecuteFolderMutationAsync(
        string accountId,
        string folderPath,
        string? callerEmployeeId,
        string operationName,
        Func<ImapClient, IMailFolder, CancellationToken, Task<SpokesResult>> action,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.");

        if (string.IsNullOrWhiteSpace(folderPath))
            return SpokesResult.Failure("Folder path cannot be empty.");

        if (ValidateFolderMutationCaller(accountId, callerEmployeeId) is { } authFailure)
            return authFailure;

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(accountId, cancellationToken);
            if (client == null)
            {
                return SpokesResult.Failure("Unable to connect or authenticate to the email server.");
            }

            var folder = await client.GetFolderAsync(folderPath, cancellationToken);
            if (folder == null)
            {
                _logger?.LogWarning("Failed to {Operation} folder '{FolderPath}' for Account {AccountId}: folder not found on IMAP server.", operationName, folderPath, accountId);
                return SpokesResult.Failure($"Folder '{folderPath}' not found on email server.");
            }

            return await action(client, folder, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to {Operation} folder {FolderPath}", operationName, folderPath);
            return SpokesResult.Failure(ex.Message);
        }
    }

    private static async Task<(IMailFolder? Folder, string? ErrorMessage)> ResolveParentFolderAsync(
        ImapClient client,
        string parentFolderPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(parentFolderPath))
        {
            var personalNamespaces = client.PersonalNamespaces;
            return personalNamespaces.Count > 0
                ? (client.GetFolder(personalNamespaces[0]), null)
                : (null, "No personal namespace found on the email server.");
        }

        return (await client.GetFolderAsync(parentFolderPath, cancellationToken), null);
    }

    internal static string GetParentPath(IMailFolder folder)
    {
        if (string.IsNullOrEmpty(folder.FullName)) return string.Empty;
        var sep = folder.DirectorySeparator.ToString();
        var lastIdx = folder.FullName.LastIndexOf(sep, StringComparison.Ordinal);
        return lastIdx > 0 ? folder.FullName[..lastIdx] : string.Empty;
    }
}
