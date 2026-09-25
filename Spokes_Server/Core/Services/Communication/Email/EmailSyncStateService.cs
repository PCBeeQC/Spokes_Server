using System;
using System.Collections.Concurrent;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

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
        => OnNewEmailReceived?.Invoke(employeeId, message);

    public void NotifyUnreadCountChanged(string employeeId)
        => OnUnreadCountChanged?.Invoke(employeeId);

    public void SetState(string employeeId, string state)
    {
        _employeeStates[employeeId] = state;
        OnStateChanged?.Invoke(employeeId);
    }

    public string GetState(string employeeId)
        => _employeeStates.TryGetValue(employeeId, out var state) ? state : "Idle";
}
