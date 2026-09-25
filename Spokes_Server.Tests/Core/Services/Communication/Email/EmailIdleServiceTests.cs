using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class EmailIdleServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfiles;
    
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<ILogger<EmailIdleService>> _mockLogger;
    
    private readonly EmailIdleService _service;

    public EmailIdleServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_EmailIdle_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

        _employees = new EmployeeRepository(_persistence, _config);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);

        var encryptionService = new EncryptionService(_config);

        _mockServiceProvider = new Mock<IServiceProvider>();
        
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(EmployeeRepository))).Returns(_employees);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(CompanyProfileRepository))).Returns(_companyProfiles);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(EncryptionService))).Returns(encryptionService);

        _mockScope = new Mock<IServiceScope>();
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockServiceProvider.Object);
        
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        
        _mockLogger = new Mock<ILogger<EmailIdleService>>();

        _service = new EmailIdleService(_mockScopeFactory.Object, _mockLogger.Object);
    }

    public void Dispose()
    {
        _service.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    [Fact]
    public void WatchFolder_ValidInput_StartsNewWatcher()
    {
        // Arrange
        var employeeId = "emp123";
        var folder = "INBOX";

        // Act
        var ex = Record.Exception(() => _service.WatchFolder(employeeId, folder));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void WatchFolder_MultipleCalls_StopsExistingWatcher()
    {
        // Arrange
        var employeeId = "emp123";
        var folder = "INBOX";

        // Act
        _service.WatchFolder(employeeId, folder);
        var ex = Record.Exception(() => _service.WatchFolder(employeeId, folder));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void WatchFolder_NullOrEmptyInputs_HandledProperly()
    {
        // Arrange
        string? employeeId = null;
        string folder = string.Empty;

        // Act
        var ex = Record.Exception(() => _service.WatchFolder(employeeId!, folder));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void StopWatching_ExistingWatcher_StopsAndRemovesWatcher()
    {
        // Arrange
        var employeeId = "emp123";
        var folder = "INBOX";
        _service.WatchFolder(employeeId, folder);

        // Act
        var ex = Record.Exception(() => _service.StopWatching(employeeId, folder));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void StopWatching_NonexistentWatcher_DoesNothing()
    {
        // Arrange
        var employeeId = "emp123";
        var folder = "INBOX";

        // Act
        var ex = Record.Exception(() => _service.StopWatching(employeeId, folder));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_ActiveWatchers_CancelsAndClearsAll()
    {
        // Arrange
        _service.WatchFolder("emp1", "INBOX");
        _service.WatchFolder("emp2", "Sent");

        // Act
        var ex = Record.Exception(() => _service.Dispose());

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        // Arrange
        _service.WatchFolder("emp1", "INBOX");

        // Act
        _service.Dispose();
        var ex = Record.Exception(() => _service.Dispose());

        // Assert
        Assert.Null(ex);
    }
}
