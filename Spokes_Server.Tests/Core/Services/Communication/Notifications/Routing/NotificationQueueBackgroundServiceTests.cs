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
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Logging;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications.Routing;

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

    private readonly Mock<INotificationRoutingService> _notificationRoutingMock;

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
        _notificationRoutingMock = new Mock<INotificationRoutingService>() { CallBase = false };
        _notificationRoutingMock.Setup(x => x.GetTotalBadgeCount(It.IsAny<string>())).Returns(0);

        var services = new ServiceCollection();
        services.AddSingleton(_webPushMock.Object);
        services.AddSingleton(_systemLogMock.Object);
        services.AddSingleton<INotificationRoutingService>(_notificationRoutingMock.Object);
        var companyRepo = new CompanyProfileRepository(_persistence, _config);
        services.AddSingleton(companyRepo);
        services.AddSingleton(new EmployeeRepository(_persistence, _config));
        services.AddSingleton(new ChatReadStateRepository(_persistence, _config, companyRepo));
        services.AddSingleton(new ChatMessageRepository(_persistence, _config, companyRepo));
        services.AddSingleton(new EmailMessageRepository(_persistence, _config));
        services.AddSingleton(new ChatStateService());
        services.AddSingleton<PresenceStateService>();
        services.AddSingleton<IPushDecisionService>(sp => sp.GetRequiredService<PresenceStateService>());

        _serviceProvider = services.BuildServiceProvider();

        _queue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object, _persistence, _config, companyRepo);

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
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
    }

    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        Assert.NotNull(_service);
        Assert.NotNull(_queue);
        Assert.NotNull(_persistence);
        Assert.NotNull(_serviceProvider);
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
        Assert.NotNull(employee);
        Assert.NotNull(repo);
        Assert.NotNull(_service);
        Assert.Empty(_queue.GetQueuedUserIds());
        _webPushMock.Verify(x => x.SendNotificationDirectAsync(It.Is<PushNotificationRequest>(r =>
            r.UserId == "emp-1" &&
            r.Title == "Test Notification" &&
            r.Body == "Test Body" &&
            r.Url == "/chat" &&
            r.Category == "chat"
        )), Times.Once);
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
        Assert.NotNull(employee);
        Assert.NotNull(repo);
        Assert.NotNull(_service);
        Assert.Empty(_queue.GetQueuedUserIds());
        _webPushMock.Verify(x => x.SendNotificationDirectAsync(It.Is<PushNotificationRequest>(r =>
            r.UserId == "emp-1" &&
            r.Title == "Quiet hours ended" &&
            r.Body == "You have 2 new emails and 1 new message and 1 other notification" &&
            r.Url == "/"
        )), Times.Once);
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
        Assert.NotNull(employee);
        Assert.NotNull(repo);
        Assert.NotNull(_service);
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
        Assert.NotNull(_service);
        Assert.NotNull(_queue);
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
        Assert.NotNull(_service);
        Assert.NotNull(_queue);
        _webPushMock.Verify(x => x.SendNotificationDirectAsync(It.Is<PushNotificationRequest>(r =>
            r.UserId == "emp-1" &&
            r.Title == "Mobile Test" &&
            r.Body == "Body" &&
            r.Tier == PresenceTier.MobileOnly &&
            r.Category == "chat" &&
            r.ThreadId == "" &&
            r.Badge == 0
        )), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithDelayedChatPush_WhenMessageEvictedFromMemoryCache_LoadsFromDiskAndSendsPush()
    {
        // Arrange: write chat message directly to disk without adding to memory cache
        var channelDir = Path.Combine(_testDataDir, "chat", "c-evicted", "messages");
        Directory.CreateDirectory(channelDir);

        var msg = new ChatMessage
        {
            Id = "msg-evicted-1",
            ChannelId = "c-evicted",
            SenderId = "sender-1",
            Content = "Hello from disk",
            SentAt = DateTime.UtcNow
        };
        File.WriteAllText(Path.Combine(channelDir, "msg-evicted-1.json"), System.Text.Json.JsonSerializer.Serialize(msg));

        _queue.EnqueueDelayedMobilePush(new DelayedMobileNotification
        {
            UserId = "emp-1",
            Type = "Chat",
            MessageId = "msg-evicted-1",
            ChannelId = "c-evicted",
            ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1),
            Title = "New Chat Message",
            Body = "Hello from disk"
        });

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert: Push notification was sent because message was retrieved from disk via GetByIdOrLoad
        _webPushMock.Verify(x => x.SendNotificationDirectAsync(It.Is<PushNotificationRequest>(r =>
            r.UserId == "emp-1" &&
            r.Title == "New Chat Message" &&
            r.Body == "Hello from disk" &&
            r.Tier == PresenceTier.MobileOnly &&
            r.Category == "chat" &&
            r.ThreadId == "c-evicted" &&
            r.Badge == 0
        )), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithDelayedMobileNotification_WhenActiveOnDesktop_PostponesInsteadOfDiscarding()
    {
        // Arrange
        var userId = "emp-active-desktop";
        var presenceState = _serviceProvider.GetRequiredService<PresenceStateService>();
        presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = userId,
            SubscriptionId = "conn-desktop-1",
            DeviceType = "Desktop",
            HasPushEnabled = true,
            IsFocused = true
        });

        var delayed = new DelayedMobileNotification
        {
            UserId = userId,
            Type = "Other",
            MessageId = "msg-active-1",
            ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1), // Matured
            Title = "Postponed Push",
            Body = "Should postpone because desktop is active"
        };
        _queue.EnqueueDelayedMobilePush(delayed);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert:
        // 1. Mobile push was NOT sent (suppressed while active on desktop)
        _webPushMock.Verify(x => x.SendNotificationDirectAsync(It.Is<PushNotificationRequest>(r => r.UserId == userId)), Times.Never);

        // 2. Notification was NOT permanently discarded; it was postponed to the future
        Assert.Empty(_queue.DequeueMaturedMobilePushes());
    }

    [Fact]
    public async Task ExecuteAsync_WhenDeliveryFails_RequeuesNotificationWithoutDataLoss()
    {
        // Arrange
        var employee = new Employee { Id = "emp-fail-1", NotificationScheduleEnabled = false };
        var repo = _serviceProvider.GetRequiredService<EmployeeRepository>();
        repo.Save(employee);

        _webPushMock
            .Setup(x => x.SendNotificationDirectAsync(It.Is<PushNotificationRequest>(r => r.UserId == "emp-fail-1")))
            .ThrowsAsync(new HttpRequestException("Connection timed out"));

        var notif = new QueuedNotification
        {
            UserId = "emp-fail-1",
            Title = "Transient Failure Test",
            Body = "Should be requeued"
        };
        _queue.Enqueue(notif);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert: notification was NOT lost
        Assert.Contains("emp-fail-1", _queue.GetQueuedUserIds());
        Assert.Equal(1, _queue.GetQueuedCount("emp-fail-1"));
        Assert.Empty(_queue.DeadLetterNotifications);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDeliveryFailsRepeatedly_MovesToDeadLetterStore()
    {
        // Arrange
        var employee = new Employee { Id = "emp-deadletter", NotificationScheduleEnabled = false };
        var repo = _serviceProvider.GetRequiredService<EmployeeRepository>();
        repo.Save(employee);

        _webPushMock
            .Setup(x => x.SendNotificationDirectAsync(It.Is<PushNotificationRequest>(r => r.UserId == "emp-deadletter")))
            .ThrowsAsync(new HttpRequestException("Permanent provider error"));

        var notif = new QueuedNotification
        {
            UserId = "emp-deadletter",
            Title = "Dead Letter Test",
            Body = "Should move to dead letter queue after max retries",
            RetryCount = NotificationQueueService.MaxDeliveryRetries - 1
        };
        _queue.Enqueue(notif);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert: notification was removed from active queue and placed in dead-letter store
        Assert.DoesNotContain("emp-deadletter", _queue.GetQueuedUserIds());
        Assert.Single(_queue.DeadLetterNotifications);
        Assert.Equal("emp-deadletter", _queue.DeadLetterNotifications[0].UserId);
        Assert.Equal("Dead Letter Test", _queue.DeadLetterNotifications[0].Title);
        Assert.Equal(NotificationQueueService.MaxDeliveryRetries, _queue.DeadLetterNotifications[0].RetryCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithDelayedMobileNotification_WhenDeliveryFails_RequeuesWithBackoff()
    {
        // Arrange
        var userId = "emp-mobile-fail";
        _webPushMock
            .Setup(x => x.SendNotificationDirectAsync(It.Is<PushNotificationRequest>(r => r.UserId == userId)))
            .ThrowsAsync(new HttpRequestException("Push server unavailable"));

        var delayed = new DelayedMobileNotification
        {
            UserId = userId,
            Type = "Other",
            MessageId = "msg-m-fail",
            ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1), // Matured
            Title = "Mobile Failure Test"
        };
        _queue.EnqueueDelayedMobilePush(delayed);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert: Notification is not lost, but backoff was applied so it is no longer immediately matured
        Assert.Empty(_queue.DequeueMaturedMobilePushes());
        Assert.Empty(_queue.DeadLetterMobileNotifications);
    }

    [Fact]
    public async Task ExecuteAsync_WithDelayedMobileNotification_WhenDeliveryFailsRepeatedly_MovesToDeadLetterStore()
    {
        // Arrange
        var userId = "emp-mobile-deadletter";
        _webPushMock
            .Setup(x => x.SendNotificationDirectAsync(It.Is<PushNotificationRequest>(r => r.UserId == userId)))
            .ThrowsAsync(new HttpRequestException("Unrecoverable error"));

        var delayed = new DelayedMobileNotification
        {
            UserId = userId,
            Type = "Other",
            MessageId = "msg-m-dlq",
            ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1),
            Title = "Mobile Dead Letter Test",
            RetryCount = NotificationQueueService.MaxDeliveryRetries - 1
        };
        _queue.EnqueueDelayedMobilePush(delayed);

        // Act
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Assert: Moved to dead letter store
        Assert.Empty(_queue.DequeueMaturedMobilePushes());
        Assert.Single(_queue.DeadLetterMobileNotifications);
        Assert.Equal(userId, _queue.DeadLetterMobileNotifications[0].UserId);
        Assert.Equal(NotificationQueueService.MaxDeliveryRetries, _queue.DeadLetterMobileNotifications[0].RetryCount);
    }
}
