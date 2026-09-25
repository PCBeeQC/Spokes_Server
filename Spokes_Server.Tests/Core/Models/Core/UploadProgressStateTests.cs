namespace Spokes_Server.Tests.Core.Models.Core;

using Spokes_Server.Core.Models.Core;

public class UploadProgressStateTests
{
    [Fact]
    public void Defaults_AreSetCorrectly()
    {
        var state = new UploadProgressState();

        Assert.Equal(0.0, state.Progress);
        Assert.Equal("Uploading...", state.StatusText);
        Assert.False(state.IsUploading);
    }

    [Fact]
    public void Progress_WhenValueChanged_UpdatesPropertyAndFiresEvent()
    {
        var state = new UploadProgressState();
        var eventFiredCount = 0;
        state.OnStateChanged += () => eventFiredCount++;

        state.Progress = 45.5;

        Assert.Equal(45.5, state.Progress);
        Assert.Equal(1, eventFiredCount);
    }

    [Fact]
    public void Progress_WhenValueUnchanged_DoesNotFireEvent()
    {
        var state = new UploadProgressState();
        var eventFiredCount = 0;
        state.OnStateChanged += () => eventFiredCount++;

        // Default is 0.0; reassigning same value should not invoke event
        state.Progress = 0.0;
        Assert.Equal(0, eventFiredCount);

        // Change value -> fires once
        state.Progress = 75.0;
        Assert.Equal(1, eventFiredCount);

        // Reassign same value -> does not fire again
        state.Progress = 75.0;
        Assert.Equal(1, eventFiredCount);
    }

    [Fact]
    public void StatusText_WhenValueChanged_UpdatesPropertyAndFiresEvent()
    {
        var state = new UploadProgressState();
        var eventFiredCount = 0;
        state.OnStateChanged += () => eventFiredCount++;

        state.StatusText = "Compressing files...";

        Assert.Equal("Compressing files...", state.StatusText);
        Assert.Equal(1, eventFiredCount);
    }

    [Fact]
    public void StatusText_WhenValueUnchanged_DoesNotFireEvent()
    {
        var state = new UploadProgressState();
        var eventFiredCount = 0;
        state.OnStateChanged += () => eventFiredCount++;

        // Default is "Uploading..."; setting same value should not invoke event
        state.StatusText = "Uploading...";
        Assert.Equal(0, eventFiredCount);

        // Change value -> fires once
        state.StatusText = "Complete";
        Assert.Equal(1, eventFiredCount);

        // Reassign same value -> does not fire again
        state.StatusText = "Complete";
        Assert.Equal(1, eventFiredCount);
    }

    [Fact]
    public void IsUploading_WhenValueChanged_UpdatesPropertyAndFiresEvent()
    {
        var state = new UploadProgressState();
        var eventFiredCount = 0;
        state.OnStateChanged += () => eventFiredCount++;

        state.IsUploading = true;

        Assert.True(state.IsUploading);
        Assert.Equal(1, eventFiredCount);
    }

    [Fact]
    public void IsUploading_WhenValueUnchanged_DoesNotFireEvent()
    {
        var state = new UploadProgressState();
        var eventFiredCount = 0;
        state.OnStateChanged += () => eventFiredCount++;

        // Default is false; setting false should not invoke event
        state.IsUploading = false;
        Assert.Equal(0, eventFiredCount);

        // Change value -> fires once
        state.IsUploading = true;
        Assert.Equal(1, eventFiredCount);

        // Reassign same value -> does not fire again
        state.IsUploading = true;
        Assert.Equal(1, eventFiredCount);
    }

    [Fact]
    public void Properties_WhenNoSubscribers_ChangingValuesDoesNotThrow()
    {
        var state = new UploadProgressState();

        var exception = Record.Exception(() =>
        {
            state.Progress = 80.0;
            state.StatusText = "Finalizing...";
            state.IsUploading = true;
        });

        Assert.Null(exception);
        Assert.Equal(80.0, state.Progress);
        Assert.Equal("Finalizing...", state.StatusText);
        Assert.True(state.IsUploading);
    }
}
