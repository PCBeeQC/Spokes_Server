using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications.Routing;

public class NotificationQueueServiceTests : IDisposable
{
    private readonly Mock<ILogger<NotificationQueueService>> _mockLogger;
    private readonly NotificationQueueService _service;

    public NotificationQueueServiceTests()
    {
        _mockLogger = new Mock<ILogger<NotificationQueueService>>();
        _service = NotificationTestFactory.CreateQueueService(_mockLogger.Object);
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

    [Theory]
    [InlineData(21, false)]
    [InlineData(22, true)]
    [InlineData(23, true)]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(12, false)]
    public void IsWithinSchedule_OvernightSchedule_EvaluatesBoundaryCorrectly(int hour, bool expectedResult)
    {
        var employee = new Employee
        {
            NotificationScheduleEnabled = true,
            NotificationSchedule =
            [
                new DaySchedule
                {
                    Day = DayOfWeek.Monday,
                    IsEnabled = true,
                    StartHour = 22,
                    EndHour = 6
                }
            ]
        };

        // 2026-01-05 is a Monday
        var refTime = new DateTime(2026, 1, 5, hour, 30, 0, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Monday, refTime.DayOfWeek);

        var result = _service.IsWithinSchedule(employee, refTime);
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void IsWithinSchedule_WithCompanyTimeZone_EvaluatesAgainstConfiguredTimeZone()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NotificationQueueTz_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configDict = new Dictionary<string, string?> { ["DataPath"] = tempDir };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            var companyRepo = new CompanyProfileRepository(persistence, config);

            var profile = companyRepo.Get();
            profile.TimeZoneId = "UTC";
            companyRepo.Save(profile);

            var service = new NotificationQueueService(
                _mockLogger.Object,
                persistence,
                config,
                companyRepo);

            var scheduleTime = service.GetCurrentScheduleTime();
            // In UTC, scheduleTime should match DateTime.UtcNow within a reasonable margin
            Assert.True((DateTime.UtcNow - scheduleTime).Duration() < TimeSpan.FromSeconds(5));

            persistence.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void GetCurrentScheduleTime_InvalidTimeZone_FallsBackToLocalServerTimeWithoutThrowing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NotificationQueueTzFallback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configDict = new Dictionary<string, string?> { ["DataPath"] = tempDir };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            var companyRepo = new CompanyProfileRepository(persistence, config);

            var profile = companyRepo.Get();
            profile.TimeZoneId = "Invalid/NonExistent_Zone_123456";
            companyRepo.Save(profile);

            var service = new NotificationQueueService(
                _mockLogger.Object,
                persistence,
                config,
                companyRepo);

            var scheduleTime = service.GetCurrentScheduleTime();
            // Should gracefully fall back to local server time
            Assert.True((DateTime.Now - scheduleTime).Duration() < TimeSpan.FromSeconds(5));

            persistence.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
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

    #region Capacity and TTL Tests

    [Fact]
    public void Enqueue_ExceedingUserLimit_EvictsOldest_FIFO()
    {
        for (int i = 0; i < NotificationQueueService.MaxQueuePerUser + 5; i++)
        {
            _service.Enqueue(new QueuedNotification
            {
                UserId = "user_capacity",
                Title = $"Title {i}",
                QueuedAtUtc = DateTime.UtcNow.AddMinutes(i)
            });
        }

        Assert.Equal(NotificationQueueService.MaxQueuePerUser, _service.GetQueuedCount("user_capacity"));

        var items = _service.DequeueAll("user_capacity");
        Assert.Equal(NotificationQueueService.MaxQueuePerUser, items.Count);
        // The first 5 (Title 0..4) should have been evicted; earliest remaining should be Title 5
        Assert.Equal("Title 5", items[0].Title);
        Assert.Equal($"Title {NotificationQueueService.MaxQueuePerUser + 4}", items[^1].Title);
    }

    [Fact]
    public void DequeueAll_ExcludesExpiredNotifications()
    {
        // One old notification beyond 24 hours TTL, one recent
        _service.Enqueue(new QueuedNotification
        {
            UserId = "user_ttl",
            Title = "Old",
            QueuedAtUtc = DateTime.UtcNow.AddHours(-25)
        });
        _service.Enqueue(new QueuedNotification
        {
            UserId = "user_ttl",
            Title = "Recent",
            QueuedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });

        var items = _service.DequeueAll("user_ttl");
        Assert.Single(items);
        Assert.Equal("Recent", items[0].Title);
    }

    [Fact]
    public void PurgeExpired_RemovesStaleQueuedAndDelayedItems()
    {
        _service.Enqueue(new QueuedNotification
        {
            UserId = "stale_user",
            Title = "Expired Queued",
            QueuedAtUtc = DateTime.UtcNow.AddHours(-30)
        });

        _service.EnqueueDelayedMobilePush(new DelayedMobileNotification
        {
            UserId = "stale_mobile_user",
            Title = "Expired Mobile",
            ProcessAtUtc = DateTime.UtcNow.AddHours(-2)
        });

        int purged = _service.PurgeExpired();
        Assert.Equal(2, purged);
        Assert.Equal(0, _service.GetQueuedCount("stale_user"));
        Assert.Empty(_service.DequeueMaturedMobilePushes());
    }

    [Fact]
    public void Persistence_SurvivesRestart_RestoresQueuedAndDelayedNotifications()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NotificationQueuePersistence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configDict = new Dictionary<string, string?> { ["DataPath"] = tempDir };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            var companyRepo = new CompanyProfileRepository(persistence, config);

            var service1 = new NotificationQueueService(
                new Mock<ILogger<NotificationQueueService>>().Object,
                persistence,
                config,
                companyRepo);

            service1.Enqueue(new QueuedNotification
            {
                UserId = "user_persist",
                Title = "Persistent Notification",
                Body = "Body 123"
            });

            service1.EnqueueDelayedMobilePush(new DelayedMobileNotification
            {
                UserId = "user_persist",
                Title = "Persistent Mobile Push",
                ProcessAtUtc = DateTime.UtcNow.AddMinutes(-2)
            });

            // Flush persistence to disk synchronously
            persistence.FlushAll();

            // Simulate container restart with a new service instance reading the same directory
            var service2 = new NotificationQueueService(
                new Mock<ILogger<NotificationQueueService>>().Object,
                persistence,
                config,
                companyRepo);

            Assert.Equal(1, service2.GetQueuedCount("user_persist"));
            var queued = service2.DequeueAll("user_persist");
            Assert.Single(queued);
            Assert.Equal("Persistent Notification", queued[0].Title);

            var delayed = service2.DequeueMaturedMobilePushes();
            Assert.Single(delayed);
            Assert.Equal("Persistent Mobile Push", delayed[0].Title);

            persistence.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void RestoreFromDisk_ExplicitCall_RestoresPersistedNotificationsIdempotently()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NotificationQueueExplicitRestore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configDict = new Dictionary<string, string?> { ["DataPath"] = tempDir };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            var companyRepo = new CompanyProfileRepository(persistence, config);

            var service1 = new NotificationQueueService(
                new Mock<ILogger<NotificationQueueService>>().Object,
                persistence,
                config,
                companyRepo);

            service1.Enqueue(new QueuedNotification
            {
                UserId = "user_explicit",
                Title = "Explicit Restore Test",
                Body = "Body 456"
            });
            persistence.FlushAll();

            var service2 = new NotificationQueueService(
                new Mock<ILogger<NotificationQueueService>>().Object,
                persistence,
                config,
                companyRepo);

            // Eagerly restore like hosted service lifecycle
            service2.RestoreFromDisk();
            // Second call is idempotent
            service2.RestoreFromDisk();

            Assert.Equal(1, service2.GetQueuedCount("user_explicit"));
            var queued = service2.DequeueAll("user_explicit");
            Assert.Single(queued);
            Assert.Equal("Explicit Restore Test", queued[0].Title);

            persistence.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void PostponeDelayedMobilePush_ValidItem_UpdatesProcessAtUtcAndPreservesOriginalQueuedAt()
    {
        // Arrange
        var initialQueuedAt = DateTime.UtcNow.AddMinutes(-10);
        var notif = new DelayedMobileNotification
        {
            UserId = "user1",
            ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1),
            QueuedAtUtc = initialQueuedAt
        };
        _service.EnqueueDelayedMobilePush(notif);

        // Act
        var result = _service.PostponeDelayedMobilePush(notif, TimeSpan.FromMinutes(5));

        // Assert
        Assert.True(result);
        Assert.Empty(_service.DequeueMaturedMobilePushes()); // Not matured now
        Assert.Equal(initialQueuedAt, notif.QueuedAtUtc);
        Assert.True(notif.ProcessAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public void PostponeDelayedMobilePush_ExpiredPastTtl_ReturnsFalse()
    {
        // Arrange
        var expiredQueuedAt = DateTime.UtcNow.Subtract(NotificationQueueService.DelayedMobileNotificationTtl).AddMinutes(-5);
        var notif = new DelayedMobileNotification
        {
            UserId = "user1",
            ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1),
            QueuedAtUtc = expiredQueuedAt
        };

        // Act
        var result = _service.PostponeDelayedMobilePush(notif, TimeSpan.FromMinutes(5));

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void DequeueMaturedMobilePushes_ReturnsInStrictFifoOrder()
    {
        // Arrange
        var notif1 = new DelayedMobileNotification { UserId = "u1", Title = "First", ProcessAtUtc = DateTime.UtcNow.AddMinutes(-10) };
        var notif2 = new DelayedMobileNotification { UserId = "u2", Title = "Second", ProcessAtUtc = DateTime.UtcNow.AddMinutes(-5) };
        var notif3 = new DelayedMobileNotification { UserId = "u3", Title = "Third", ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1) };

        // Enqueue out of order
        _service.EnqueueDelayedMobilePush(notif3);
        _service.EnqueueDelayedMobilePush(notif1);
        _service.EnqueueDelayedMobilePush(notif2);

        // Act
        var matured = _service.DequeueMaturedMobilePushes();

        // Assert: FIFO order by ProcessAtUtc
        Assert.Equal(3, matured.Count);
        Assert.Equal("First", matured[0].Title);
        Assert.Equal("Second", matured[1].Title);
        Assert.Equal("Third", matured[2].Title);
    }

    [Fact]
    public void LeaseDueNotifications_SetsLeasedUntil_AndDoesNotRemoveUntilAcknowledged()
    {
        // Arrange
        var notif1 = new QueuedNotification { UserId = "u_lease", Title = "Lease 1" };
        var notif2 = new QueuedNotification { UserId = "u_lease", Title = "Lease 2" };
        _service.Enqueue(notif1);
        _service.Enqueue(notif2);

        // Act 1: Lease items
        var leased = _service.LeaseDueNotifications("u_lease", TimeSpan.FromMinutes(2));
        Assert.Equal(2, leased.Count);
        Assert.All(leased, item => Assert.NotNull(item.LeasedUntilUtc));

        // Act 2: Immediate re-lease before lease expiry returns nothing
        var reLeased = _service.LeaseDueNotifications("u_lease", TimeSpan.FromMinutes(2));
        Assert.Empty(reLeased);

        // Act 3: Acknowledge first item
        _service.AcknowledgeDelivery("u_lease", new[] { notif1.Id });
        Assert.Equal(1, _service.GetQueuedCount("u_lease"));

        // Act 4: Requeue second item on failure
        _service.RequeueOnFailure("u_lease", new[] { notif2.Id }, new Exception("Network error"));
        Assert.Equal(1, _service.GetQueuedCount("u_lease"));

        // Act 5: Because lease was cleared on requeue, it can be leased again immediately
        var leasedAgain = _service.LeaseDueNotifications("u_lease", TimeSpan.FromMinutes(2));
        Assert.Single(leasedAgain);
        Assert.Equal(1, leasedAgain[0].RetryCount);
    }

    [Fact]
    public void RequeueOnFailure_WhenMaxRetriesExceeded_TransfersToDeadLetterQueue()
    {
        // Arrange
        var notif = new QueuedNotification
        {
            UserId = "u_dlq",
            Title = "Fail repeatedly",
            RetryCount = NotificationQueueService.MaxDeliveryRetries - 1
        };
        _service.Enqueue(notif);

        // Act
        _service.RequeueOnFailure("u_dlq", new[] { notif.Id }, new Exception("Fatal delivery failure"));

        // Assert
        Assert.Equal(0, _service.GetQueuedCount("u_dlq"));
        Assert.Single(_service.DeadLetterNotifications);
        Assert.Equal(notif.Id, _service.DeadLetterNotifications[0].Id);
        Assert.Equal(NotificationQueueService.MaxDeliveryRetries, _service.DeadLetterNotifications[0].RetryCount);
    }

    [Fact]
    public void LeaseMaturedMobilePushes_SetsLeasedUntil_AndDoesNotRemoveUntilAcknowledged()
    {
        // Arrange
        var delayed = new DelayedMobileNotification
        {
            UserId = "u_m_lease",
            Title = "Matured",
            ProcessAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        _service.EnqueueDelayedMobilePush(delayed);

        // Act 1: Lease
        var leased = _service.LeaseMaturedMobilePushes(TimeSpan.FromMinutes(2));
        Assert.Single(leased);
        Assert.NotNull(leased[0].LeasedUntilUtc);

        // Immediate subsequent lease yields empty
        var reLeased = _service.LeaseMaturedMobilePushes(TimeSpan.FromMinutes(2));
        Assert.Empty(reLeased);

        // Acknowledge delivery
        _service.AcknowledgeMobilePushDelivery(delayed.Id);

        // Queue should now be empty even after lease expiration
        Assert.Empty(_service.DequeueMaturedMobilePushes());
    }

    [Fact]
    public void RequeueMobilePushOnFailure_WhenMaxRetriesExceeded_TransfersToDeadLetterQueue()
    {
        // Arrange
        var delayed = new DelayedMobileNotification
        {
            UserId = "u_m_dlq",
            Title = "Mobile DLQ",
            ProcessAtUtc = DateTime.UtcNow.AddMinutes(-1),
            RetryCount = NotificationQueueService.MaxDeliveryRetries - 1
        };
        _service.EnqueueDelayedMobilePush(delayed);

        // Act
        _service.RequeueMobilePushOnFailure(delayed.Id, new Exception("Mobile push failed"));

        // Assert
        Assert.Empty(_service.DequeueMaturedMobilePushes());
        Assert.Single(_service.DeadLetterMobileNotifications);
        Assert.Equal(delayed.Id, _service.DeadLetterMobileNotifications[0].Id);
        Assert.Equal(NotificationQueueService.MaxDeliveryRetries, _service.DeadLetterMobileNotifications[0].RetryCount);
    }

    #endregion
}
