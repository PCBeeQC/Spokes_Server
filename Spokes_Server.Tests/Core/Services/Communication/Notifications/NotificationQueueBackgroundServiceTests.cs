using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Logging;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications;

public class NotificationQueueBackgroundServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IWebPushService> _webPushMock;
    private readonly Mock<ISystemLogService> _systemLogMock;
    private readonly NotificationQueueService _queue;
    private readonly NotificationQueueBackgroundService _service;

    private readonly Mock<NotificationRoutingService> _notificationRoutingMock;

    public NotificationQueueBackgroundServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_NotificationQueue_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

        _webPushMock = new Mock<IWebPushService>();
        _systemLogMock = new Mock<ISystemLogService>();

        // Mock NotificationRoutingService for badge recalculation in FlushDelayedMobileNotificationsAsync
        _notificationRoutingMock = new Mock<NotificationRoutingService>(
            MockBehavior.Loose,
            /* constructor params: */ null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) { CallBase = false };
        _notificationRoutingMock.Setup(x => x.GetTotalBadgeCountAsync(It.IsAny<string>())).ReturnsAsync(0);

        var services = new ServiceCollection();
        services.AddSingleton(_webPushMock.Object);
        services.AddSingleton(_systemLogMock.Object);
        services.AddSingleton(_notificationRoutingMock.Object);
        var companyRepo = new CompanyProfileRepository(_persistence, _config);
        services.AddSingleton(companyRepo);
        services.AddSingleton(new EmployeeRepository(_persistence, _config));
        services.AddSingleton(new ChatReadStateRepository(_persistence, _config, companyRepo));
        services.AddSingleton(new ChatMessageRepository(_persistence, _config, companyRepo));
        services.AddSingleton(new EmailMessageRepository(_persistence, _config));
        services.AddSingleton(new ChatStateService());
        services.AddSingleton<PresenceStateService>();

        _serviceProvider = services.BuildServiceProvider();

        _queue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object);

        _service = new NotificationQueueBackgroundService(
            _queue,
            _serviceProvider,
            new Mock<ILogger<NotificationQueueBackgroundService>>().Object);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _serviceProvider.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithDueNotifications_SendsWebPushAndClearsQueue()
    {
        // Arrange
        var employee = new Employee { Id = "emp-1", NotificationScheduleEnabled = false };
        var repo = _serviceProvider.GetRequiredService<EmployeeRepository>();
        repo.Save(employee);

        _queue.Enqueue(new QueuedNotification
        {
            UserId = "emp-1",
            Title = "Test Notification",
            Body = "Test Body",
            Url = "/chat"
        });

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Empty(_queue.GetQueuedUserIds());
        _webPushMock.Verify(x => x.SendNotificationDirectAsync(
            "emp-1", "Test Notification", "Test Body", "/chat", null, null, null, null, "chat", null, null, null, false, null, false, null), 
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleDueNotifications_SendsSummaryNotification()
    {
        // Arrange
        var employee = new Employee { Id = "emp-1", NotificationScheduleEnabled = false };
        var repo = _serviceProvider.GetRequiredService<EmployeeRepository>();
        repo.Save(employee);

        _queue.Enqueue(new QueuedNotification { UserId = "emp-1", Title = "1", Url = "/chat/1" });
        _queue.Enqueue(new QueuedNotification { UserId = "emp-1", Title = "2", Url = "/email/1" });
        _queue.Enqueue(new QueuedNotification { UserId = "emp-1", Title = "3", Url = "/email/2" });
        _queue.Enqueue(new QueuedNotification { UserId = "emp-1", Title = "4", Url = "/other" });

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Empty(_queue.GetQueuedUserIds());
        _webPushMock.Verify(x => x.SendNotificationDirectAsync(
            "emp-1", "Quiet hours ended", "You have 2 new emails and 1 new message and 1 other notification", "/", null, null, null, null, "chat", null, null, null, false, null, false, null), 
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_UserOutsideSchedule_LeavesQueueIntact()
    {
        // Arrange
        var employee = new Employee 
        { 
            Id = "emp-1", 
            NotificationScheduleEnabled = true,
            NotificationSchedule =
            [
                new DaySchedule 
                { 
                    Day = DateTime.Now.DayOfWeek, 
                    IsEnabled = true, 
                    StartHour = (DateTime.Now.Hour + 2) % 24, // Future hour
                    EndHour = (DateTime.Now.Hour + 3) % 24
                }
            ]
        };
        var repo = _serviceProvider.GetRequiredService<EmployeeRepository>();
        repo.Save(employee);

        _queue.Enqueue(new QueuedNotification { UserId = "emp-1", Title = "Test" });

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Single(_queue.GetQueuedUserIds()); // Still in queue
        _webPushMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_DeletedUser_DiscardsQueue()
    {
        // Arrange
        // Employee is NOT added to repo (deleted/missing)
        _queue.Enqueue(new QueuedNotification { UserId = "emp-missing", Title = "Test" });

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Empty(_queue.GetQueuedUserIds()); // Discarded
        _webPushMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WithDelayedMobileNotifications_SendsMobilePush()
    {
        // Arrange
        _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification
        {
            UserId = "emp-1",
            Type = "Other",
            MessageId = "msg-1",
            ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1), // Matured
            Title = "Mobile Test",
            Body = "Body"
        });

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert — badge is now recalculated at flush time (mock returns 0)
        _webPushMock.Verify(x => x.SendNotificationDirectAsync(
            "emp-1", "Mobile Test", "Body", null, null, PresenceTier.MobileOnly, null, null, "chat", "", null, null, false, 0, false, null), 
            Times.Once);
    }
}
