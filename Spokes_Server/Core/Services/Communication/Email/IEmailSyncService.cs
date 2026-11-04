using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Handles IMAP mailbox synchronization for connected EmailAccounts and individual folders.
/// </summary>
public interface IEmailSyncService
{
    Task SyncAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task SyncSingleFolderAsync(string accountId, string folderPath, CancellationToken cancellationToken = default);
}
