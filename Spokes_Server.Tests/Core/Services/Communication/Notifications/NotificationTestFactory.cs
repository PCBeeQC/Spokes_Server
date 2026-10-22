using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications;

public static class NotificationTestFactory
{
    public static NotificationQueueService CreateQueueService(
        ILogger<NotificationQueueService>? logger = null,
        DiskPersistenceService? persistence = null,
        IConfiguration? config = null,
        CompanyProfileRepository? companyRepo = null,
        string? dataPath = null)
    {
        var tempDir = dataPath ?? Path.Combine(Path.GetTempPath(), $"NotificationQueueTest_{Guid.NewGuid():N}");
        var finalConfig = config ?? new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = tempDir })
            .Build();
        var finalPersistence = persistence ?? new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        var finalCompanyRepo = companyRepo ?? new CompanyProfileRepository(finalPersistence, finalConfig);
        var finalLogger = logger ?? new Mock<ILogger<NotificationQueueService>>().Object;

        return new NotificationQueueService(finalLogger, finalPersistence, finalConfig, finalCompanyRepo);
    }
}
