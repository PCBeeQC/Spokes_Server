using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailFolderService
{
    Task<SpokesResult<EmailFolder>> CreateFolderAsync(string employeeId, string parentFolderPath, string newFolderName, CancellationToken cancellationToken = default);
    Task<SpokesResult> DeleteFolderAsync(string employeeId, string folderPath, CancellationToken cancellationToken = default);
    Task<SpokesResult> RenameFolderAsync(string employeeId, string folderPath, string newName, CancellationToken cancellationToken = default);
}
