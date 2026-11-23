using System;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailCacheMaintenanceService
{
    string EmailBodyPath { get; }
    string GetAttachmentPath(string accountId, string messageId, string attachmentId);
    string? GetMimeFilePath(string accountId, string messageId);
    Task<string> GetEmailBodyAsync(string accountId, string messageId, CancellationToken cancellationToken = default);
    Task RepairEmailInstallationAsync(string accountId, Func<string, CancellationToken, Task> syncAccountCallback, string? callerEmployeeId = null, CancellationToken cancellationToken = default);
    Task DeleteLocalMessageFilesAsync(string accountId, string messageId);
}
