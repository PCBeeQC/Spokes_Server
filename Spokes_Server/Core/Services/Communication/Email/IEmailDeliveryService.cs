using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public interface IEmailDeliveryService
{
    Task SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default);
}
