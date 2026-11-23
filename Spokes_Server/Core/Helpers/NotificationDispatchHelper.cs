using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// Shared fire-and-forget background dispatcher for notification routing and delivery,
/// isolating caller workflows from background exceptions while preserving structured logging.
/// </summary>
public static class NotificationDispatchHelper
{
    public static Task SafeDispatchAsync(
        Func<Task> action,
        string operation,
        ILogger? logger,
        LogLevel failureLogLevel = LogLevel.Error,
        string logMessageTemplate = "Failed to dispatch {Operation} in background")
    {
        return Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                logger?.Log(failureLogLevel, ex, logMessageTemplate, operation);
            }
        });
    }
}
