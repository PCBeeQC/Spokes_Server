using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailDraftService
{
    Task<string> SaveDraftAsync(SaveDraftRequest request, CancellationToken cancellationToken = default);
    Task RemoveDraftAttachmentAsync(string employeeId, string draftId, string attachmentId, CancellationToken cancellationToken = default);
    Task RepairLocalDraftsAsync(string employeeId, CancellationToken cancellationToken = default);
}
