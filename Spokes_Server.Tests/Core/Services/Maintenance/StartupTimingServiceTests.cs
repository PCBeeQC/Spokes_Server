
namespace Spokes_Server.Tests.Core.Services.Maintenance;

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

        // DIAG-TEMP: chat transition diagnostics
        [Fact]
        public void ChatTransition_NoTransition_IgnoresMarks()
        {
            var service = new StartupTimingService();
            Assert.Equal(0, service.CurrentTransitionId);

            service.AddChatTransition("IgnoredStep");
            var result = service.Drain(1, 0);
            Assert.Empty(result.Entries);
        }

        [Fact]
        public void ChatTransition_DrainIsIncrementalAndIncludesRegularAddSteps()
        {
            var service = new StartupTimingService();

            var tid = service.EnsureTransition("SelectChannel (general)", "Server: SelectChannel Clicked (general)");
            Assert.Equal(tid, service.CurrentTransitionId);

            service.AddChatTransition("Server: SelectChannelInternal Start");
            service.Add("Server: RenderTree Chat Markup Start");

            var pass1 = service.Drain(tid, 0);
            Assert.Equal("SelectChannel (general)", pass1.TransitionName);
            Assert.Equal(4, pass1.Entries.Count); // "Transition begun" + 3 steps
            Assert.Equal("Server: SelectChannel Clicked (general)", pass1.Entries[1].Name);
            Assert.Equal("Server: RenderTree Chat Markup Start", pass1.Entries[3].Name);

            service.AddChatTransition("Server: MainLayout.HandleLocationChanged (_isChatChannelOpen=True)");
            var pass2 = service.Drain(tid, pass1.NextCursor);
            Assert.Single(pass2.Entries);
            Assert.Equal("Server: MainLayout.HandleLocationChanged (_isChatChannelOpen=True)", pass2.Entries[0].Name);
        }

        [Fact]
        public void ChatTransition_ClientBegunTransition_IsReusedAndNamedByEnsure()
        {
            var service = new StartupTimingService();

            var clientTid = service.BeginTransition("SelectChannel", isNamed: false);
            var serverTid = service.EnsureTransition("SelectChannel (general)", "Server: SelectChannel Clicked (general)");

            Assert.Equal(clientTid, serverTid);
            Assert.Equal("SelectChannel (general)", service.Drain(clientTid, 0).TransitionName);
        }

        [Fact]
        public void ChatTransition_SpansTrackOpenCountAndStayAttributedToOriginalTransition()
        {
            var service = new StartupTimingService();
            var first = service.BeginTransition("First");
            var span = service.Span("Server: Chain", first);
            Assert.Equal(1, service.Drain(first, 0).OpenSpans);

            var second = service.BeginTransition("Second");
            span.Dispose();

            var firstResult = service.Drain(first, 0);
            Assert.Equal(0, firstResult.OpenSpans);
            Assert.Contains(firstResult.Entries, e => e.Name.StartsWith("Server: Chain End"));
            Assert.DoesNotContain(service.Drain(second, 0).Entries, e => e.Name.StartsWith("Server: Chain"));
        }
    }
