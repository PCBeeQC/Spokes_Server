using System;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailCacheMaintenanceService
{
    string EmailBodyPath { get; }
    string GetAttachmentPath(string requestingEmployeeId, string messageId, string attachmentId);
    string? GetMimeFilePath(string employeeId, string messageId);
    Task<string> GetEmailBodyAsync(string requestingEmployeeId, string messageId, CancellationToken cancellationToken = default);
    Task<string> GetSignatureAsync(string employeeId, CancellationToken cancellationToken = default);
    Task SaveSignatureAsync(string employeeId, string html, CancellationToken cancellationToken = default);
    Task RepairEmailInstallationAsync(string employeeId, Func<string, CancellationToken, Task> syncEmployeeCallback, CancellationToken cancellationToken = default);
    Task DeleteLocalMessageFilesAsync(string employeeId, string messageId);
}
