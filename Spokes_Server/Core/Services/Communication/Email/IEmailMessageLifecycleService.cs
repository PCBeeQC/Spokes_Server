using System;
using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailMessageLifecycleService
{
    Task<EmailMessage?> GetMessageAsync(string employeeId, string messageId);
    Task<SpokesResult> MarkMessageReadAsync(string employeeId, string messageId, bool isRead, CancellationToken cancellationToken = default);
    Task<SpokesResult> MarkMessageFlaggedAsync(string employeeId, string messageId, bool isFlagged, CancellationToken cancellationToken = default);
    Task<SpokesResult> MoveMessageAsync(string employeeId, string messageId, string destinationFolderPath, CancellationToken cancellationToken = default);
    Task<SpokesResult> MoveMessageToTrashAsync(string employeeId, string messageId, CancellationToken cancellationToken = default);
    Task<SpokesResult> ArchiveMessageAsync(string employeeId, string messageId, CancellationToken cancellationToken = default);
    Task<SpokesResult> JunkMessageAsync(string employeeId, string messageId, CancellationToken cancellationToken = default);
    Task<SpokesResult> DeleteMessageAsync(string employeeId, string messageId, CancellationToken cancellationToken = default);
}
