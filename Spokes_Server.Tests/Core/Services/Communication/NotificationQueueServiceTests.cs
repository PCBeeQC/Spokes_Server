using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.Logging;
using Moq;

namespace Spokes_Server.Tests.Core.Services.Communication
{
    public class NotificationQueueServiceTests
    {
        private readonly NotificationQueueService _service;

        public NotificationQueueServiceTests()
        {
            _service = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object);
        }

        [Fact]
        public void IsWithinSchedule_ReturnsTrue_WhenScheduleDisabled()
        {
            var employee = new Employee
            {
                NotificationScheduleEnabled = false
            };
            Assert.True(_service.IsWithinSchedule(employee));
        }

        [Fact]
        public void IsWithinSchedule_ReturnsTrue_WhenInsideActiveHours()
        {
            var now = DateTime.Now;
            var employee = new Employee
            {
                NotificationScheduleEnabled = true,
                NotificationSchedule = Enumerable.Range(0, 7).Select(i => new DaySchedule
                {
                    Day = (DayOfWeek)i,
                    IsEnabled = true,
                    StartHour = 0,
                    EndHour = 24  // All day enabled
                }).ToList()
            };
            Assert.True(_service.IsWithinSchedule(employee));
        }

        [Fact]
        public void IsWithinSchedule_ReturnsFalse_WhenOutsideActiveHours()
        {
            var now = DateTime.Now;
            // Set a window that definitely does NOT include the current hour
            // by using a 1-hour window at an hour guaranteed to not be current
            var impossibleHour = (now.Hour + 12) % 24;

            var employee = new Employee
            {
                NotificationScheduleEnabled = true,
                NotificationSchedule = Enumerable.Range(0, 7).Select(i => new DaySchedule
                {
                    Day = (DayOfWeek)i,
                    IsEnabled = true,
                    StartHour = impossibleHour,
                    EndHour = (impossibleHour + 1) % 24 == 0 ? 24 : (impossibleHour + 1) % 24
                }).ToList()
            };
            // The window is a single hour 12 hours away from now, so this must be false
            Assert.False(_service.IsWithinSchedule(employee));
        }

        [Fact]
        public void IsWithinSchedule_ReturnsFalse_WhenDayDisabled()
        {
            var today = DateTime.Now.DayOfWeek;
            var employee = new Employee
            {
                NotificationScheduleEnabled = true,
                NotificationSchedule = Enumerable.Range(0, 7).Select(i => new DaySchedule
                {
                    Day = (DayOfWeek)i,
                    IsEnabled = (DayOfWeek)i != today, // Disable today
                    StartHour = 0,
                    EndHour = 24
                }).ToList()
            };
            Assert.False(_service.IsWithinSchedule(employee));
        }

        [Fact]
        public void IsWithinSchedule_ReturnsTrue_WhenNoScheduleEntryForToday()
        {
            // Empty schedule list — should default to true
            var employee = new Employee
            {
                NotificationScheduleEnabled = true,
                NotificationSchedule = new List<DaySchedule>()
            };
            Assert.True(_service.IsWithinSchedule(employee));
        }

        [Fact]
        public void Enqueue_And_DequeueAll_WorkCorrectly()
        {
            var notification = new QueuedNotification
            {
                UserId = "user1",
                Title = "Test",
                Body = "Test body"
            };

            _service.Enqueue(notification);
            Assert.Equal(1, _service.GetQueuedCount("user1"));

            var items = _service.DequeueAll("user1");
            Assert.Single(items);
            Assert.Equal("Test", items[0].Title);

            // Queue should be empty now
            Assert.Equal(0, _service.GetQueuedCount("user1"));
        }

        [Fact]
        public void DequeueAll_ReturnsEmpty_WhenNoItems()
        {
            var items = _service.DequeueAll("nonexistent");
            Assert.Empty(items);
        }

        [Fact]
        public void GetQueuedUserIds_ReturnsCorrectIds()
        {
            _service.Enqueue(new QueuedNotification { UserId = "user1", Title = "A", Body = "B" });
            _service.Enqueue(new QueuedNotification { UserId = "user2", Title = "C", Body = "D" });

            var ids = _service.GetQueuedUserIds().ToList();
            Assert.Contains("user1", ids);
            Assert.Contains("user2", ids);
        }
    }
}
