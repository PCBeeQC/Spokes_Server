using System;
using System.Collections.Generic;
using System.IO;

namespace Spokes_Server.Core.Services.Maintenance;

/// <summary>
/// Shared file scanner for enumerating server data files eligible for backup.
/// Used by both local archive backups and direct file-tree remote destinations (such as GitHub).
/// </summary>
public static class BackupFileScanner
{
    public static (List<(FileInfo File, string RelativePath)> Files, long TotalBytes) ScanCandidateFiles(
        string dataPath,
        bool includeEmployeeEmails)
    {
        var candidateFiles = new List<(FileInfo File, string RelativePath)>();
        long totalBytes = 0;

        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return (candidateFiles, 0);
        }

        var dataDir = new DirectoryInfo(dataPath);
        if (!dataDir.Exists)
        {
            return (candidateFiles, 0);
        }

        foreach (var file in dataDir.GetFiles("*", SearchOption.AllDirectories))
        {
            // Skip the Backups folder and temp files
            var relativePath = Path.GetRelativePath(dataPath, file.FullName);
            if (relativePath.StartsWith("Backups", StringComparison.OrdinalIgnoreCase) &&
                (relativePath.Length == 7 || relativePath[7] == Path.DirectorySeparatorChar || relativePath[7] == Path.AltDirectorySeparatorChar))
            {
                continue;
            }

            if (file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedRelPath = relativePath.Replace('\\', '/');

            if (!includeEmployeeEmails && IsExcludedEmployeeEmailFile(normalizedRelPath))
            {
                continue;
            }

            candidateFiles.Add((file, normalizedRelPath));
            totalBytes += file.Length;
        }

        return (candidateFiles, totalBytes);
    }

    internal static bool IsExcludedEmployeeEmailFile(string normalizedRelPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedRelPath))
        {
            return false;
        }

        // Legacy employee-centric mailbox storage: Employees/{EmployeeId}/Email/...
        if (normalizedRelPath.StartsWith("Employees/", StringComparison.OrdinalIgnoreCase) &&
            normalizedRelPath.Contains("/Email/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Legacy pre-migration top-level mailbox directories
        if (normalizedRelPath.StartsWith("EmailFolders/", StringComparison.OrdinalIgnoreCase) ||
            normalizedRelPath.StartsWith("EmailMessages/", StringComparison.OrdinalIgnoreCase) ||
            normalizedRelPath.StartsWith("Emails/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Current account-centric storage: EmailAccounts/{AccountId}/...
        // Preserve EmailAccounts/{AccountId}/Account.json (account configuration),
        // but exclude synced mailbox contents (Messages/, Bodies/, Mime/, Attachments/, Folders/, index.json).
        const string emailAccountsPrefix = "EmailAccounts/";
        if (normalizedRelPath.StartsWith(emailAccountsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = normalizedRelPath.AsSpan(emailAccountsPrefix.Length);
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex > 0)
            {
                var afterAccountId = remainder[(slashIndex + 1)..];
                if (afterAccountId.Equals("Account.json", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }
}
