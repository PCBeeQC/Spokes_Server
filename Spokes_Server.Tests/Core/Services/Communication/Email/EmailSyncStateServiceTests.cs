using System;
using Xunit;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Email;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class EmailSyncStateServiceTests : IDisposable
{
    private readonly EmailSyncStateService _service = new();

    public void Dispose()
    {
        // Nothing to dispose for this service
    }

    [Fact]
    public void NotifyNewEmail_ValidInput_FiresEvent()
    {
        // Arrange
        var employeeId = "emp_123";
        var message = new EmailMessage { Id = "msg_1", Subject = "Test" };
        string? invokedEmployeeId = null;
        EmailMessage? invokedMessage = null;
        
        _service.OnNewEmailReceived += (emp, msg) => 
        {
            invokedEmployeeId = emp;
            invokedMessage = msg;
        };

        // Act
        _service.NotifyNewEmail(employeeId, message);

        // Assert
        Assert.Equal(employeeId, invokedEmployeeId);
        Assert.Equal(message, invokedMessage);
    }

    [Fact]
    public void NotifyNewEmail_NoSubscribers_DoesNotThrow()
    {
        // Arrange
        var employeeId = "emp_123";
        var message = new EmailMessage { Id = "msg_1", Subject = "Test" };

        // Act & Assert (Should not throw NullReferenceException)
        var exception = Record.Exception(() => _service.NotifyNewEmail(employeeId, message));
        Assert.Null(exception);
    }

    [Fact]
    public void NotifyUnreadCountChanged_ValidInput_FiresEvent()
    {
        // Arrange
        var employeeId = "emp_123";
        string? invokedEmployeeId = null;
        
        _service.OnUnreadCountChanged += emp => invokedEmployeeId = emp;

        // Act
        _service.NotifyUnreadCountChanged(employeeId);

        // Assert
        Assert.Equal(employeeId, invokedEmployeeId);
    }

    [Fact]
    public void NotifyUnreadCountChanged_NoSubscribers_DoesNotThrow()
    {
        // Arrange
        var employeeId = "emp_123";

        // Act & Assert
        var exception = Record.Exception(() => _service.NotifyUnreadCountChanged(employeeId));
        Assert.Null(exception);
    }

    [Fact]
    public void SetState_ValidInput_UpdatesStateAndFiresEvent()
    {
        // Arrange
        var employeeId = "emp_123";
        var state = "Syncing";
        string? invokedEmployeeId = null;
        
        _service.OnStateChanged += emp => invokedEmployeeId = emp;

        // Act
        _service.SetState(employeeId, state);

        // Assert
        Assert.Equal(employeeId, invokedEmployeeId);
        Assert.Equal(state, _service.GetState(employeeId));
    }

    [Fact]
    public void SetState_MultipleUpdates_MaintainsCorrectState()
    {
        // Arrange
        var employeeId = "emp_123";
        var state1 = "Syncing";
        var state2 = "Idle";
        var state3 = "Error";
        var eventCount = 0;
        
        _service.OnStateChanged += emp => 
        {
            if (emp == employeeId) eventCount++;
        };

        // Act
        _service.SetState(employeeId, state1);
        _service.SetState(employeeId, state2);
        _service.SetState(employeeId, state3);

        // Assert
        Assert.Equal(3, eventCount);
        Assert.Equal(state3, _service.GetState(employeeId));
    }

    [Fact]
    public void SetState_NoSubscribers_DoesNotThrow()
    {
        // Arrange
        var employeeId = "emp_123";
        var state = "Syncing";

        // Act & Assert
        var exception = Record.Exception(() => _service.SetState(employeeId, state));
        Assert.Null(exception);
        Assert.Equal(state, _service.GetState(employeeId));
    }

    [Fact]
    public void GetState_UnknownEmployee_ReturnsIdle()
    {
        // Arrange
        var employeeId = "unknown_emp";

        // Act
        var result = _service.GetState(employeeId);

        // Assert
        Assert.Equal("Idle", result);
    }
    
    [Fact]
    public void GetState_AfterSetState_ReturnsSetValue()
    {
        // Arrange
        var employeeId = "emp_456";
        var state = "Connecting";
        _service.SetState(employeeId, state);

        // Act
        var result = _service.GetState(employeeId);

        // Assert
        Assert.Equal(state, result);
    }
}
