using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

public class StartupTimingServiceTests
    {
        [Fact]
        public void GetTimings_Initially_ReturnsEmptyList()
        {
            // Arrange
            var service = new StartupTimingService();

            // Act
            var timings = service.GetTimings();

            // Assert
            Assert.NotNull(timings);
            Assert.Empty(timings);
        }

        [Fact]
        public void Add_SingleMilestone_RecordsNameAndPositiveTimestamp()
        {
            // Arrange
            var service = new StartupTimingService();
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Act
            service.Add("InitMilestone");
            var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Assert
            var timings = service.GetTimings();
            Assert.Single(timings);
            Assert.Equal("InitMilestone", timings[0].Name);
            Assert.True(timings[0].Timestamp >= before);
            Assert.True(timings[0].Timestamp <= after);
        }

        [Fact]
        public void Add_MultipleMilestones_PreservesChronologicalOrder()
        {
            // Arrange
            var service = new StartupTimingService();

            // Act
            service.Add("Step1");
            service.Add("Step2");
            service.Add("Step3");

            // Assert
            var timings = service.GetTimings();
            Assert.Equal(3, timings.Count);
            Assert.Equal("Step1", timings[0].Name);
            Assert.Equal("Step2", timings[1].Name);
            Assert.Equal("Step3", timings[2].Name);
            Assert.True(timings[0].Timestamp <= timings[1].Timestamp);
            Assert.True(timings[1].Timestamp <= timings[2].Timestamp);
        }

        [Fact]
        public void GetTimings_ReturnsSnapshotCopy_ModifyingCopyDoesNotAffectService()
        {
            // Arrange
            var service = new StartupTimingService();
            service.Add("Original");

            // Act
            var copy = service.GetTimings();
            copy.Clear();
            copy.Add(new StartupTiming { Name = "Tampered", Timestamp = 9999 });

            // Assert
            var originalTimings = service.GetTimings();
            Assert.Single(originalTimings);
            Assert.Equal("Original", originalTimings[0].Name);
        }

        [Fact]
        public async Task Add_ConcurrentCalls_ThreadSafetyVerification()
        {
            // Arrange
            var service = new StartupTimingService();
            const int count = 50;

            // Act - Run 50 parallel tasks adding milestones concurrently
            var tasks = Enumerable.Range(0, count)
                .Select(i => Task.Run(() => service.Add($"Milestone_{i}")))
                .ToArray();

            await Task.WhenAll(tasks);

            // Assert
            var timings = service.GetTimings();
            Assert.Equal(count, timings.Count);
            var expectedNames = Enumerable.Range(0, count).Select(i => $"Milestone_{i}").ToHashSet();
            var actualNames = timings.Select(t => t.Name).ToHashSet();
            Assert.Equal(expectedNames, actualNames);
        }

        [Fact]
        public void StartupTiming_PropertyGettersSetters_WorkCorrectly()
        {
            // Arrange & Act - Default constructor values
            var defaultTiming = new StartupTiming();

            // Assert defaults
            Assert.Equal(string.Empty, defaultTiming.Name);
            Assert.Equal(0L, defaultTiming.Timestamp);

            // Act - Setting properties
            var timing = new StartupTiming
            {
                Name = "CustomMilestone",
                Timestamp = 1234567890L
            };

            // Assert assigned values
            Assert.Equal("CustomMilestone", timing.Name);
            Assert.Equal(1234567890L, timing.Timestamp);
        }
    }
