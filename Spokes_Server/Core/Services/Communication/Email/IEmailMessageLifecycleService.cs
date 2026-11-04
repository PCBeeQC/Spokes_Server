using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailMessageLifecycleService
{
    Task<EmailMessage?> GetMessageAsync(string accountId, string messageId);
    Task<SpokesResult> MarkMessageReadAsync(string accountId, string messageId, bool isRead, CancellationToken cancellationToken = default);
    Task<SpokesResult> MarkMessageFlaggedAsync(string accountId, string messageId, bool isFlagged, CancellationToken cancellationToken = default);
    Task<SpokesResult> MoveMessageAsync(string accountId, string messageId, string destinationFolderPath, CancellationToken cancellationToken = default);
    Task<SpokesResult> MoveMessageToTrashAsync(string accountId, string messageId, CancellationToken cancellationToken = default);
    Task<SpokesResult> ArchiveMessageAsync(string accountId, string messageId, CancellationToken cancellationToken = default);
    Task<SpokesResult> JunkMessageAsync(string accountId, string messageId, CancellationToken cancellationToken = default);
    Task<SpokesResult> DeleteMessageAsync(string accountId, string messageId, CancellationToken cancellationToken = default);
}
