using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using System.Collections.Concurrent;

namespace Spokes_Server.Core.Services.Communication.Email;

using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

/// <summary>
/// Singleton service to track the real-time background sync state of an employee's email.
/// Components like the EmailSidebar can subscribe to OnStateChanged to update the UI.
/// </summary>
public class EmailSyncStateService
{
    private readonly ConcurrentDictionary<string, string> _employeeStates = new();

    public event Action<string>? OnStateChanged;
    public event Action<string, EmailMessage>? OnNewEmailReceived;
    public event Action<string>? OnUnreadCountChanged;

    public void NotifyNewEmail(string employeeId, EmailMessage message)
    {
        OnNewEmailReceived?.Invoke(employeeId, message);
    }

    public void NotifyUnreadCountChanged(string employeeId)
    {
        OnUnreadCountChanged?.Invoke(employeeId);
    }

    public void SetState(string employeeId, string state)
    {
        _employeeStates[employeeId] = state;
        OnStateChanged?.Invoke(employeeId);
    }

    public string GetState(string employeeId)
    {
        if (_employeeStates.TryGetValue(employeeId, out var state))
        {
            return state;
        }
        return "Idle";
    }
}

