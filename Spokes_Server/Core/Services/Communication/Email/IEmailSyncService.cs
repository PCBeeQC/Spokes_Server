using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Handles IMAP mailbox synchronization for employees and individual folders.
/// </summary>
public interface IEmailSyncService
{
    Task SyncEmployeeAsync(string employeeId, CancellationToken cancellationToken = default);
    Task SyncSingleFolderAsync(string employeeId, string folderPath, CancellationToken cancellationToken = default);
}
