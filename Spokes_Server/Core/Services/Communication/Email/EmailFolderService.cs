using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailFolderService : IEmailFolderService
{
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly EncryptionService _encryptionService;
    private readonly ILogger<EmailFolderService>? _logger;
    private readonly IImapConnectionFactory _imapFactory;

    public EmailFolderService(
        EmployeeRepository employees,
        CompanyProfileRepository companyProfile,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        EncryptionService encryptionService,
        ILogger<EmailFolderService>? logger = null,
        IImapConnectionFactory? imapFactory = null)
    {
        _employees = employees;
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _encryptionService = encryptionService;
        _logger = logger;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, employees, encryptionService);
    }

    public async Task<SpokesResult<EmailFolder>> CreateFolderAsync(string employeeId, string parentFolderPath, string newFolderName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult<EmailFolder>.Failure("Employee ID is required.");

        if (string.IsNullOrWhiteSpace(newFolderName))
            return SpokesResult<EmailFolder>.Failure("Folder name cannot be empty.");

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(employeeId, cancellationToken);
            if (client == null)
            {
                return SpokesResult<EmailFolder>.Failure("Unable to connect or authenticate to the email server. Please check your credentials.");
            }

            IMailFolder? parentFolder;
            if (string.IsNullOrEmpty(parentFolderPath))
            {
                var personalNamespaces = client.PersonalNamespaces;
                if (personalNamespaces.Count > 0)
                    parentFolder = client.GetFolder(personalNamespaces[0]);
                else
                    return SpokesResult<EmailFolder>.Failure("No personal namespace found on the email server.");
            }
            else
            {
                parentFolder = await client.GetFolderAsync(parentFolderPath, cancellationToken);
            }

            if (parentFolder == null)
            {
                _logger?.LogWarning("Failed to create folder '{FolderName}' for Employee {EmployeeId}: parent folder was not found on IMAP server.", newFolderName, employeeId);
                return SpokesResult<EmailFolder>.Failure($"Parent folder '{parentFolderPath}' was not found on the email server.");
            }

            var newFolder = await parentFolder.CreateAsync(newFolderName, true, cancellationToken);
            if (newFolder == null)
            {
                _logger?.LogWarning("Failed to create folder '{FolderName}' for Employee {EmployeeId}: IMAP folder creation returned null.", newFolderName, employeeId);
                return SpokesResult<EmailFolder>.Failure($"Email server failed to create folder '{newFolderName}'.");
            }

            var existingFolders = _emailFolders.GetByEmployee(employeeId);
            var localFolder = existingFolders.FirstOrDefault(f => f.Path == newFolder.FullName);
            if (localFolder == null)
            {
                localFolder = new EmailFolder
                {
                    EmployeeId = employeeId,
                    Name = newFolder.Name,
                    Path = newFolder.FullName,
                    ParentPath = GetParentPath(newFolder),
                    Delimiter = newFolder.DirectorySeparator.ToString(),
                    IsInbox = newFolder.Attributes.HasFlag(FolderAttributes.Inbox),
                    IsSent = newFolder.Attributes.HasFlag(FolderAttributes.Sent),
                    IsTrash = newFolder.Attributes.HasFlag(FolderAttributes.Trash),
                    IsDrafts = newFolder.Attributes.HasFlag(FolderAttributes.Drafts),
                    IsArchive = newFolder.Attributes.HasFlag(FolderAttributes.Archive),
                    IsJunk = newFolder.Attributes.HasFlag(FolderAttributes.Junk)
                };
                if (newFolder.Name.Equals("Inbox", StringComparison.OrdinalIgnoreCase)) localFolder.IsInbox = true;
                _emailFolders.Save(localFolder);
            }

            await client.DisconnectAsync(true, cancellationToken);

            var localFolders = _emailFolders.GetByEmployee(employeeId);
            var created = localFolders.FirstOrDefault(f => f.Path == newFolder.FullName);
            return created != null
                ? SpokesResult<EmailFolder>.Success(created)
                : SpokesResult<EmailFolder>.Failure("Folder was created on server but could not be saved locally.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create folder {FolderName}", newFolderName);
            return SpokesResult<EmailFolder>.Failure(ex.Message);
        }
    }

    public async Task<SpokesResult> DeleteFolderAsync(string employeeId, string folderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult.Failure("Employee ID is required.");

        if (string.IsNullOrWhiteSpace(folderPath))
            return SpokesResult.Failure("Folder path cannot be empty.");

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(employeeId, cancellationToken);
            if (client == null)
            {
                return SpokesResult.Failure("Unable to connect or authenticate to the email server.");
            }

            var folder = await client.GetFolderAsync(folderPath, cancellationToken);
            if (folder == null)
            {
                _logger?.LogWarning("Failed to delete folder '{FolderPath}' for Employee {EmployeeId}: folder not found on IMAP server.", folderPath, employeeId);
                return SpokesResult.Failure($"Folder '{folderPath}' not found on email server.");
            }
            await folder.DeleteAsync(cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            var folders = _emailFolders.GetByEmployee(employeeId);
            var local = folders.FirstOrDefault(f => f.Path == folderPath);
            if (local != null) _emailFolders.Delete(local.Id);

            return SpokesResult.Success();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete folder {FolderPath}", folderPath);
            return SpokesResult.Failure(ex.Message);
        }
    }

    public async Task<SpokesResult> RenameFolderAsync(string employeeId, string folderPath, string newName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult.Failure("Employee ID is required.");

        if (string.IsNullOrWhiteSpace(folderPath))
            return SpokesResult.Failure("Folder path cannot be empty.");

        if (string.IsNullOrWhiteSpace(newName))
            return SpokesResult.Failure("New folder name cannot be empty.");

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(employeeId, cancellationToken);
            if (client == null)
            {
                return SpokesResult.Failure("Unable to connect or authenticate to the email server.");
            }

            var folder = await client.GetFolderAsync(folderPath, cancellationToken);
            if (folder == null)
            {
                _logger?.LogWarning("Failed to rename folder '{FolderPath}' for Employee {EmployeeId}: folder not found on IMAP server.", folderPath, employeeId);
                return SpokesResult.Failure($"Folder '{folderPath}' not found on email server.");
            }
            var parentPath = GetParentPath(folder);
            IMailFolder? parentFolder;
            if (string.IsNullOrEmpty(parentPath))
            {
                var personalNamespaces = client.PersonalNamespaces;
                parentFolder = client.GetFolder(personalNamespaces[0]);
            }
            else
            {
                parentFolder = await client.GetFolderAsync(parentPath, cancellationToken);
            }

            if (parentFolder == null)
            {
                _logger?.LogWarning("Failed to rename folder '{FolderPath}' for Employee {EmployeeId}: parent folder not found on IMAP server.", folderPath, employeeId);
                return SpokesResult.Failure("Parent folder not found on email server.");
            }

            await folder.RenameAsync(parentFolder, newName, cancellationToken);
            var renamedFolder = await parentFolder.GetSubfolderAsync(newName, cancellationToken);
            if (renamedFolder == null)
            {
                _logger?.LogWarning("Failed to retrieve renamed folder '{NewName}' for Employee {EmployeeId}.", newName, employeeId);
                return SpokesResult.Failure($"Failed to retrieve renamed folder '{newName}' from email server.");
            }

            await client.DisconnectAsync(true, cancellationToken);

            var folders = _emailFolders.GetByEmployee(employeeId);
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

                _emailMessages.RenameFolder(employeeId, oldPath, renamedFolder.FullName);
            }

            return SpokesResult.Success();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to rename folder {FolderPath}", folderPath);
            return SpokesResult.Failure(ex.Message);
        }
    }

    private static string GetParentPath(IMailFolder folder)
    {
        if (string.IsNullOrEmpty(folder.FullName)) return string.Empty;
        var sep = folder.DirectorySeparator.ToString();
        var lastIdx = folder.FullName.LastIndexOf(sep, StringComparison.Ordinal);
        return lastIdx > 0 ? folder.FullName.Substring(0, lastIdx) : string.Empty;
    }
}
