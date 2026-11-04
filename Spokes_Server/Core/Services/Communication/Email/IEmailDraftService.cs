using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailDraftService
{
    Task<string> SaveDraftAsync(SaveDraftRequest request, CancellationToken cancellationToken = default);
    Task RemoveDraftAttachmentAsync(string accountId, string draftId, string attachmentId, CancellationToken cancellationToken = default);
    Task RemoveDraftAttachmentAsync(string accountId, string draftId, string attachmentId, string? employeeId, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(string accountId, string draftId, string? employeeId, CancellationToken cancellationToken = default);
    Task RepairLocalDraftsAsync(string accountId, CancellationToken cancellationToken = default);
}
