using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class TestEmailBackgroundService : EmailBackgroundService
{
    public int PollCount { get; private set; }
    
    public TestEmailBackgroundService(IServiceProvider serviceProvider, ILogger<EmailBackgroundService> logger, EmailIdleService idleService) 
        : base(serviceProvider, logger, idleService)
    {
    }

    public List<Employee> PublicGetSyncEmployees(EmployeeRepository repo)
    {
        return base.GetSyncEmployees(repo);
    }

    protected override Task WaitBeforeNextPollAsync(CancellationToken stoppingToken)
    {
        PollCount++;
        return Task.Delay(10, stoppingToken);
    }

    protected override Task WaitOnStartupAsync(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }
}

public class EmailBackgroundServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly ServiceCollection _services;
    private readonly ServiceProvider _serviceProvider;
    
    private readonly EmployeeRepository _employeesRepo;
    private readonly EmailFolderRepository _foldersRepo;
    private readonly EmailMessageRepository _messagesRepo;
    private readonly CompanyProfileRepository _companyProfileRepo;
    
    private readonly Mock<EmailService> _mockEmailService;
    private readonly EmailIdleService _idleService;
    private readonly Mock<ILogger<EmailBackgroundService>> _mockLogger;
    
    private readonly TestEmailBackgroundService _service;

    public EmailBackgroundServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_EmailBG_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        
        _employeesRepo = new EmployeeRepository(_persistence, _config);
        _foldersRepo = new EmailFolderRepository(_persistence, _config);
        _messagesRepo = new EmailMessageRepository(_persistence, _config);
        _companyProfileRepo = new CompanyProfileRepository(_persistence, _config);
        
        // Mock EmailService (can pass nulls since we only mock virtual methods)
        _mockEmailService = new Mock<EmailService>(null, null, null, null, null, null, null, null, null);
        
        _services = new ServiceCollection();
        _services.AddSingleton(_employeesRepo);
        _services.AddSingleton(_foldersRepo);
        _services.AddSingleton(_messagesRepo);
        _services.AddSingleton(_companyProfileRepo);
        
        // For EmailIdleService dependencies
        _services.AddSingleton(new EncryptionService(_config));
        
        _services.AddSingleton(_mockEmailService.Object);
        
        _serviceProvider = _services.BuildServiceProvider();
        
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        mockScope.Setup(s => s.ServiceProvider).Returns(_serviceProvider);
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        
        _idleService = new EmailIdleService(mockScopeFactory.Object, new Mock<ILogger<EmailIdleService>>().Object);
        
        _mockLogger = new Mock<ILogger<EmailBackgroundService>>();
        _service = new TestEmailBackgroundService(_serviceProvider, _mockLogger.Object, _idleService);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _idleService.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    [Fact]
    public void GetSyncEmployees_ReturnsEmployeesWithEmailAndPassword()
    {
        // Arrange
        _employeesRepo.Save(new Employee { Id = "emp1", Email = "a@b.com", EncryptedEmailPassword = "pwd" });
        _employeesRepo.Save(new Employee { Id = "emp2", Email = "", EncryptedEmailPassword = "pwd" }); // No email
        _employeesRepo.Save(new Employee { Id = "emp3", Email = "c@d.com", EncryptedEmailPassword = "" }); // No pwd
        _employeesRepo.Save(new Employee { Id = "emp4", Email = null, EncryptedEmailPassword = null });

        // Act
        var result = _service.PublicGetSyncEmployees(_employeesRepo);

        // Assert
        Assert.Single(result);
        Assert.Equal("emp1", result[0].Id);
    }

    [Fact]
    public void GetSyncEmployees_EmptyRepo_ReturnsEmptyList()
    {
        // Arrange & Act
        var result = _service.PublicGetSyncEmployees(_employeesRepo);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteAsync_SyncsEmployeesAndReconcilesFolders()
    {
        // Arrange
        var empId = "emp_exec";
        _employeesRepo.Save(new Employee { Id = empId, Email = "test@test.com", EncryptedEmailPassword = "pwd", FirstName = "Test" });
        
        var folder = new EmailFolder { Id = "f1", EmployeeId = empId, Path = "INBOX", IsInbox = true, UnreadCount = 0 };
        _foldersRepo.Save(folder);
        
        // Add an unread message to the local index so reconcile fixes the count
        _messagesRepo.Save(new EmailMessage { Id = "m1", EmployeeId = empId, FolderPath = "INBOX", IsRead = false, UniqueId = 1 });

        _mockEmailService.Setup(s => s.SyncEmployeeAsync(empId)).Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        
        // Act
        // Start the service and cancel it shortly after to allow 1-2 loop iterations
        var executeTask = _service.StartAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        try { await _service.StopAsync(CancellationToken.None); } catch (Exception) { }

        // Assert
        _mockEmailService.Verify(s => s.SyncEmployeeAsync(empId), Times.AtLeastOnce);
        
        var updatedFolder = _foldersRepo.GetByEmployee(empId).FirstOrDefault(f => f.Path == "INBOX");
        Assert.NotNull(updatedFolder);
        Assert.Equal(1, updatedFolder.UnreadCount); // Should be reconciled
    }
    
    [Fact]
    public async Task OnIdleMessageReceived_TriggersSyncSingleFolder()
    {
        // Arrange
        var empId = "emp_idle1";
        var folderPath = "INBOX";
        
        _mockEmailService.Setup(s => s.SyncSingleFolderAsync(empId, folderPath)).Returns(Task.CompletedTask);

        // Act - Trigger event using reflection
        var fieldInfo = typeof(EmailIdleService).GetField("MessageReceived", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (fieldInfo?.GetValue(_idleService) is MulticastDelegate multicastDelegate)
        {
            foreach (var handler in multicastDelegate.GetInvocationList())
            {
                handler.DynamicInvoke(empId, folderPath, UniqueId.MinValue);
            }
        }

        // Allow Task.Run inside the event handler to complete
        await Task.Delay(100);

        // Assert
        _mockEmailService.Verify(s => s.SyncSingleFolderAsync(empId, folderPath), Times.Once);
    }

    [Fact]
    public async Task OnIdleFlagsChanged_TriggersSyncSingleFolder()
    {
        // Arrange
        var empId = "emp_idle2";
        var folderPath = "Sent";
        
        _mockEmailService.Setup(s => s.SyncSingleFolderAsync(empId, folderPath)).Returns(Task.CompletedTask);

        // Act - Trigger event using reflection
        var fieldInfo = typeof(EmailIdleService).GetField("MessageFlagsChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (fieldInfo?.GetValue(_idleService) is MulticastDelegate multicastDelegate)
        {
            foreach (var handler in multicastDelegate.GetInvocationList())
            {
                handler.DynamicInvoke(empId, folderPath, UniqueId.MinValue);
            }
        }

        // Allow Task.Run inside the event handler to complete
        await Task.Delay(100);

        // Assert
        _mockEmailService.Verify(s => s.SyncSingleFolderAsync(empId, folderPath), Times.Once);
    }
}
