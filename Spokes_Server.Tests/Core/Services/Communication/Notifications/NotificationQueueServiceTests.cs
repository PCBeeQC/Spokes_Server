using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications;

public class NotificationQueueServiceTests : IDisposable
{
    private readonly Mock<ILogger<NotificationQueueService>> _mockLogger;
    private readonly NotificationQueueService _service;

    public NotificationQueueServiceTests()
    {
        _mockLogger = new Mock<ILogger<NotificationQueueService>>();
        _service = new NotificationQueueService(_mockLogger.Object);
    }

    public void Dispose()
    {
        // No unmanaged resources or file cleanup needed for this service
    }

    #region IsWithinSchedule Tests

    [Fact]
    public void IsWithinSchedule_ScheduleDisabled_ReturnsTrue()
    {
        // Arrange
        var employee = new Employee { NotificationScheduleEnabled = false };

        // Act
        var result = _service.IsWithinSchedule(employee);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsWithinSchedule_EnabledButDayDisabled_ReturnsFalse()
    {
        // Arrange
        var employee = new Employee { NotificationScheduleEnabled = true };
        foreach (var day in employee.NotificationSchedule)
        {
            day.IsEnabled = false;
        }

        // Act
        var result = _service.IsWithinSchedule(employee);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsWithinSchedule_EnabledAndAllHoursAllowed_ReturnsTrue()
    {
        // Arrange
        var employee = new Employee { NotificationScheduleEnabled = true };
        foreach (var day in employee.NotificationSchedule)
        {
            day.IsEnabled = true;
            day.StartHour = 0;
            day.EndHour = 24;
        }

        // Act
        var result = _service.IsWithinSchedule(employee);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsWithinSchedule_EnabledAndNoHoursAllowed_ReturnsFalse()
    {
        // Arrange
        var employee = new Employee { NotificationScheduleEnabled = true };
        foreach (var day in employee.NotificationSchedule)
        {
            day.IsEnabled = true;
            day.StartHour = 0;
            day.EndHour = 0;
        }

        // Act
        var result = _service.IsWithinSchedule(employee);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsWithinSchedule_NullScheduleDay_ReturnsTrue()
    {
        // Arrange
        var employee = new Employee 
        { 
            NotificationScheduleEnabled = true,
            NotificationSchedule = [] // Empty list, so FirstOrDefault returns null
        };

        // Act
        var result = _service.IsWithinSchedule(employee);

        // Assert
        Assert.True(result);
    }

    #endregion

    #region Enqueue Tests

    [Fact]
    public void Enqueue_ValidNotification_AddsToQueue()
    {
        // Arrange
        var notification = new QueuedNotification { UserId = "user1", Title = "Test" };

        // Act
        _service.Enqueue(notification);

        // Assert
        Assert.Equal(1, _service.GetQueuedCount("user1"));
        var userIds = _service.GetQueuedUserIds();
        Assert.Contains("user1", userIds);
    }

    [Fact]
    public void Enqueue_MultipleNotificationsSameUser_AddsToSameQueue()
    {
        // Arrange
        var notif1 = new QueuedNotification { UserId = "user1", Title = "Test1" };
        var notif2 = new QueuedNotification { UserId = "user1", Title = "Test2" };

        // Act
        _service.Enqueue(notif1);
        _service.Enqueue(notif2);

        // Assert
        Assert.Equal(2, _service.GetQueuedCount("user1"));
    }

    #endregion

    #region DequeueAll Tests

    [Fact]
    public void DequeueAll_ExistingUser_ReturnsAndRemovesNotifications()
    {
        // Arrange
        _service.Enqueue(new QueuedNotification { UserId = "user1", Title = "Test1" });
        _service.Enqueue(new QueuedNotification { UserId = "user1", Title = "Test2" });

        // Act
        var result = _service.DequeueAll("user1");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(0, _service.GetQueuedCount("user1"));
        Assert.DoesNotContain("user1", _service.GetQueuedUserIds());
    }

    [Fact]
    public void DequeueAll_NonExistingUser_ReturnsEmptyList()
    {
        // Arrange
        _service.Enqueue(new QueuedNotification { UserId = "user2", Title = "Test2" });

        // Act
        var result = _service.DequeueAll("user1");

        // Assert
        Assert.Empty(result);
        Assert.Equal(1, _service.GetQueuedCount("user2"));
    }

    #endregion

    #region GetQueuedUserIds Tests

    [Fact]
    public void GetQueuedUserIds_EmptyQueue_ReturnsEmpty()
    {
        // Act
        var result = _service.GetQueuedUserIds();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetQueuedUserIds_MultipleUsers_ReturnsAllKeys()
    {
        // Arrange
        _service.Enqueue(new QueuedNotification { UserId = "user1" });
        _service.Enqueue(new QueuedNotification { UserId = "user2" });
        _service.Enqueue(new QueuedNotification { UserId = "user1" });

        // Act
        var result = _service.GetQueuedUserIds().ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("user1", result);
        Assert.Contains("user2", result);
    }

    #endregion

    #region GetQueuedCount Tests

    [Fact]
    public void GetQueuedCount_NonExistingUser_ReturnsZero()
    {
        // Act
        var count = _service.GetQueuedCount("missing_user");

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetQueuedCount_ExistingUser_ReturnsCorrectCount()
    {
        // Arrange
        _service.Enqueue(new QueuedNotification { UserId = "user1" });
        _service.Enqueue(new QueuedNotification { UserId = "user1" });

        // Act
        var count = _service.GetQueuedCount("user1");

        // Assert
        Assert.Equal(2, count);
    }

    #endregion

    #region EnqueueDelayedMobilePush Tests

    [Fact]
    public void EnqueueDelayedMobilePush_ValidNotification_AddsToQueue()
    {
        // Arrange
        var notif = new DelayedMobileNotification { UserId = "user1", ProcessAtUtc = DateTime.UtcNow.AddMinutes(5) };

        // Act
        _service.EnqueueDelayedMobilePush(notif);

        // Assert (No direct property to verify count, but we can verify it doesn't dequeue immediately)
        var matured = _service.DequeueMaturedMobilePushes();
        Assert.Empty(matured);
    }

    [Fact]
    public void EnqueueDelayedMobilePush_Multiple_AddsAll()
    {
        // Arrange
        var notif1 = new DelayedMobileNotification { UserId = "user1", ProcessAtUtc = DateTime.UtcNow.AddMinutes(-5) };
        var notif2 = new DelayedMobileNotification { UserId = "user2", ProcessAtUtc = DateTime.UtcNow.AddMinutes(-10) };

        // Act
        _service.EnqueueDelayedMobilePush(notif1);
        _service.EnqueueDelayedMobilePush(notif2);
        
        var matured = _service.DequeueMaturedMobilePushes();

        // Assert
        Assert.Equal(2, matured.Count);
    }

    #endregion

    #region DequeueMaturedMobilePushes Tests

    [Fact]
    public void DequeueMaturedMobilePushes_WithPastDate_ReturnsAndRemoves()
    {
        // Arrange
        var notif = new DelayedMobileNotification { UserId = "user1", ProcessAtUtc = DateTime.UtcNow.AddMinutes(-5) };
        _service.EnqueueDelayedMobilePush(notif);

        // Act
        var result = _service.DequeueMaturedMobilePushes();

        // Assert
        Assert.Single(result);
        Assert.Equal("user1", result[0].UserId);

        // Verify it was removed
        var secondResult = _service.DequeueMaturedMobilePushes();
        Assert.Empty(secondResult);
    }

    [Fact]
    public void DequeueMaturedMobilePushes_WithFutureDate_ReturnsEmpty()
    {
        // Arrange
        var notif = new DelayedMobileNotification { UserId = "user1", ProcessAtUtc = DateTime.UtcNow.AddMinutes(5) };
        _service.EnqueueDelayedMobilePush(notif);

        // Act
        var result = _service.DequeueMaturedMobilePushes();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void DequeueMaturedMobilePushes_MixedDates_ReturnsOnlyMatured()
    {
        // Arrange
        var notifFuture = new DelayedMobileNotification { UserId = "user1", ProcessAtUtc = DateTime.UtcNow.AddMinutes(5) };
        var notifPast = new DelayedMobileNotification { UserId = "user2", ProcessAtUtc = DateTime.UtcNow.AddMinutes(-5) };
        var notifPast2 = new DelayedMobileNotification { UserId = "user3", ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1) };

        _service.EnqueueDelayedMobilePush(notifFuture);
        _service.EnqueueDelayedMobilePush(notifPast);
        _service.EnqueueDelayedMobilePush(notifPast2);

        // Act
        var result = _service.DequeueMaturedMobilePushes();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.UserId == "user2");
        Assert.Contains(result, r => r.UserId == "user3");
        Assert.DoesNotContain(result, r => r.UserId == "user1");
    }

    #endregion
}
