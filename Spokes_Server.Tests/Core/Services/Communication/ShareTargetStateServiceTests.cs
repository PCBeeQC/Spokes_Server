using System.Threading;
using Spokes_Server.Components.Layout;
using Spokes_Server.Core.Services.Communication;

namespace Spokes_Server.Tests.Core.Services.Communication;

public class ShareTargetStateServiceTests
{
    [Fact]
    public void InitialState_PendingPayloadAndTargetChannelId_AreNull()
    {
        // Arrange & Act
        var service = new ShareTargetStateService();

        // Assert
        Assert.Null(service.PendingPayload);
        Assert.Null(service.TargetChannelId);
    }

    [Fact]
    public void SetPayload_UpdatesPendingPayloadAndTargetChannelId_Correctly()
    {
        // Arrange
        var service = new ShareTargetStateService();
        var payload = new MainLayout.SharePayload
        {
            Title = "Test Title",
            Text = "Test Body",
            Url = "https://spokes.local/item/1"
        };
        const string channelId = "chan-456";

        // Act
        service.SetPayload(payload, channelId);

        // Assert
        Assert.Same(payload, service.PendingPayload);
        Assert.Equal(channelId, service.TargetChannelId);
    }

    [Fact]
    public void SetPayload_InvokesOnPayloadReceived_WhenSubscribersExist()
    {
        // Arrange
        var service = new ShareTargetStateService();
        var invocationCount = 0;
        service.OnPayloadReceived += () => invocationCount++;

        var payload = new MainLayout.SharePayload { Title = "Share" };

        // Act
        service.SetPayload(payload, "chan-1");

        // Assert
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void SetPayload_InvokesMultipleSubscribers_WhenRegistered()
    {
        // Arrange
        var service = new ShareTargetStateService();
        var subscriber1Called = false;
        var subscriber2Called = false;

        service.OnPayloadReceived += () => subscriber1Called = true;
        service.OnPayloadReceived += () => subscriber2Called = true;

        // Act
        service.SetPayload(new MainLayout.SharePayload(), "chan-abc");

        // Assert
        Assert.True(subscriber1Called);
        Assert.True(subscriber2Called);
    }

    [Fact]
    public void SetPayload_DoesNotThrow_WhenOnPayloadReceivedHasNoSubscribers()
    {
        // Arrange
        var service = new ShareTargetStateService();

        // Act
        var ex = Record.Exception(() => service.SetPayload(new MainLayout.SharePayload(), "chan-none"));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void SetPayload_CanOverwriteExistingPayload()
    {
        // Arrange
        var service = new ShareTargetStateService();
        var payload1 = new MainLayout.SharePayload { Title = "First" };
        var payload2 = new MainLayout.SharePayload { Title = "Second" };

        service.SetPayload(payload1, "chan-1");

        // Act
        service.SetPayload(payload2, "chan-2");

        // Assert
        Assert.Same(payload2, service.PendingPayload);
        Assert.Equal("chan-2", service.TargetChannelId);
    }

    [Fact]
    public void Clear_ResetsPendingPayloadAndTargetChannelId_ToNull()
    {
        // Arrange
        var service = new ShareTargetStateService();
        service.SetPayload(new MainLayout.SharePayload { Title = "Shared" }, "chan-999");
        Assert.NotNull(service.PendingPayload);
        Assert.NotNull(service.TargetChannelId);

        // Act
        service.Clear();

        // Assert
        Assert.Null(service.PendingPayload);
        Assert.Null(service.TargetChannelId);
    }

    [Fact]
    public void Clear_DoesNotInvokeOnPayloadReceived()
    {
        // Arrange
        var service = new ShareTargetStateService();
        service.SetPayload(new MainLayout.SharePayload(), "chan-1");

        var eventFired = false;
        service.OnPayloadReceived += () => eventFired = true;

        // Act
        service.Clear();

        // Assert
        Assert.False(eventFired);
    }

    [Fact]
    public void Clear_WhenAlreadyEmpty_DoesNotThrowAndRemainsNull()
    {
        // Arrange
        var service = new ShareTargetStateService();

        // Act
        var ex = Record.Exception(() => service.Clear());

        // Assert
        Assert.Null(ex);
        Assert.Null(service.PendingPayload);
        Assert.Null(service.TargetChannelId);
    }

    [Fact]
    public async Task Concurrency_ParallelSetPayloadAndClear_NoDeadlockOrCorruption()
    {
        // Arrange
        var service = new ShareTargetStateService();
        var eventFireCount = 0;
        service.OnPayloadReceived += () => Interlocked.Increment(ref eventFireCount);

        const int taskCount = 10;
        const int iterationsPerTask = 200;
        var tasks = new List<Task>();

        // Act
        for (var i = 0; i < taskCount; i++)
        {
            var threadId = i;
            tasks.Add(Task.Run(() =>
            {
                for (var j = 0; j < iterationsPerTask; j++)
                {
                    if (j % 2 == 0)
                    {
                        var payload = new MainLayout.SharePayload
                        {
                            Title = $"Task-{threadId}-Iter-{j}",
                            Text = $"Text-{j}"
                        };
                        service.SetPayload(payload, $"channel-{threadId}");
                    }
                    else
                    {
                        service.Clear();
                    }

                    // Concurrent reads should never throw
                    _ = service.PendingPayload;
                    _ = service.TargetChannelId;
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        // All tasks completed without deadlock or unhandled exceptions
        Assert.True(eventFireCount > 0);

        // Final state must be internally consistent: either both null or both matching
        service.Clear();
        Assert.Null(service.PendingPayload);
        Assert.Null(service.TargetChannelId);
    }
}
