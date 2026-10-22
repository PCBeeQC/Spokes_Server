using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;

namespace Spokes_Server.Tests.Helpers;

/// <summary>
/// Dedicated test mock fixture for constructing lightweight Database instances in unit tests.
/// Avoids exposing partial constructors on the production Database class that violate non-nullable property contracts.
/// </summary>
public static class TestDatabaseFactory
{
    public static Database Create(
        DeviceSessionRepository? deviceSessions = null,
        EmployeeRepository? employees = null,
        Action<Mock<Database>>? configure = null)
    {
        return CreateMock(deviceSessions, employees, configure).Object;
    }

    public static Mock<Database> CreateMock(
        DeviceSessionRepository? deviceSessions = null,
        EmployeeRepository? employees = null,
        Action<Mock<Database>>? configure = null)
    {
        var mock = new Mock<Database>();
        var mockCore = new Mock<CoreContext>();
        if (deviceSessions != null)
        {
            mock.SetupGet(d => d.DeviceSessions).Returns(deviceSessions);
            mockCore.SetupGet(c => c.DeviceSessions).Returns(deviceSessions);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"Spokes_TestDb_{Guid.NewGuid():N}");
        var persistence = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = tempPath })
            .Build();

        var companyProfiles = new CompanyProfileRepository(persistence, config);
        var serverConfigs = new ServerConfigRepository(persistence, config);

        mock.SetupGet(d => d.CompanyProfile).Returns(companyProfiles);
        mockCore.SetupGet(c => c.CompanyProfile).Returns(companyProfiles);
        mock.SetupGet(d => d.ServerConfigs).Returns(serverConfigs);
        mockCore.SetupGet(c => c.ServerConfigs).Returns(serverConfigs);

        mock.SetupGet(d => d.CoreContext).Returns(mockCore.Object);

        var mockHr = new Mock<HrContext>();
        if (employees != null)
        {
            mock.SetupGet(d => d.Employees).Returns(employees);
            mockHr.SetupGet(h => h.Employees).Returns(employees);
        }
        mock.SetupGet(d => d.HrContext).Returns(mockHr.Object);

        configure?.Invoke(mock);
        return mock;
    }
}
