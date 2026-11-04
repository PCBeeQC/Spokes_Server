using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailDeliveryService
{
    Task SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default);
    Task SendSystemBillInboxEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default);
}
