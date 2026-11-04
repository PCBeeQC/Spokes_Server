using System;
using System.Collections.Concurrent;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Singleton service to track real-time background sync state per EmailAccount.
/// </summary>
public class EmailSyncStateService
{
    private readonly ConcurrentDictionary<string, string> _accountStates = new();

    public event Action<string>? OnStateChanged;
    public event Action<string, EmailMessage>? OnNewEmailReceived;
    public event Action<string>? OnUnreadCountChanged;

    public void NotifyNewEmail(string accountId, EmailMessage message)
        => OnNewEmailReceived?.Invoke(accountId, message);

    public void NotifyUnreadCountChanged(string accountId)
        => OnUnreadCountChanged?.Invoke(accountId);

    public void SetState(string accountId, string state)
    {
        _accountStates[accountId] = state;
        OnStateChanged?.Invoke(accountId);
    }

    public string GetState(string accountId)
        => _accountStates.TryGetValue(accountId, out var state) ? state : "Idle";

    public void RemoveAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        _accountStates.TryRemove(accountId, out _);
        NotifyUnreadCountChanged(accountId);
    }
}
