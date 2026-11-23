using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailFolderService
{
    Task<SpokesResult<EmailFolder>> CreateFolderAsync(string accountId, string parentFolderPath, string newFolderName, string? callerEmployeeId = null, CancellationToken cancellationToken = default);
    Task<SpokesResult> DeleteFolderAsync(string accountId, string folderPath, string? callerEmployeeId = null, CancellationToken cancellationToken = default);
    Task<SpokesResult> RenameFolderAsync(string accountId, string folderPath, string newName, string? callerEmployeeId = null, CancellationToken cancellationToken = default);
}
