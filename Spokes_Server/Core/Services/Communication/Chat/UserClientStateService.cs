namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Hubs;
using Spokes_Server.Core.Models.Communication;

/// <summary>
/// Singleton service managing transient mobile client state.
/// Drafts are held purely in memory during typing and only flushed to disk via Db.ClientStates
/// when the user backgrounds, disconnects, or closes the app before sending.
/// </summary>
public class UserClientStateService
{
    private readonly Database _db;
    private readonly ChatStateService _chatState;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<UserClientStateService> _logger;

    // In-memory drafts: Key: (UserId, ChannelId), Value: Draft message
    private readonly ConcurrentDictionary<(string UserId, string ChannelId), string> _inMemoryDrafts = new();

    // Active channel per user: Key: UserId, Value: ChannelId
    private readonly ConcurrentDictionary<string, string> _activeChannels = new();

    // Active typing channel per user: Key: UserId, Value: ChannelId
    private readonly ConcurrentDictionary<string, string> _activeTypingChannels = new();

    // Tracks channels that have already been auto-opened for a user, to prevent repeated forced navigation. Key: (UserId, ChannelId)
    private readonly ConcurrentDictionary<(string UserId, string ChannelId), bool> _autoOpenedDrafts = new();

    /// <summary>
    /// Parameterless constructor for mocking frameworks.
    /// </summary>
    protected UserClientStateService()
    {
        _db = null!;
        _chatState = null!;
        _hubContext = null!;
        _logger = null!;
    }

    public UserClientStateService(
        Database db,
        ChatStateService chatState,
        IHubContext<ChatHub> hubContext,
        ILogger<UserClientStateService> logger)
    {
        _db = db;
        _chatState = chatState;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Updates draft text in memory. ZERO disk writes happen during typing.
    /// </summary>
    public virtual void UpdateDraftInMemory(string userId, string channelId, string draftText)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(channelId)) return;

        _activeChannels[userId] = channelId;

        if (string.IsNullOrWhiteSpace(draftText))
        {
            _inMemoryDrafts.TryRemove((userId, channelId), out _);
            _autoOpenedDrafts.TryRemove((userId, channelId), out _);
            _db.ClientStates.ClearDraft(userId, channelId);
        }
        else
        {
            _inMemoryDrafts[(userId, channelId)] = draftText;
            _autoOpenedDrafts.TryRemove((userId, channelId), out _);
        }
    }

    /// <summary>
    /// Marks a draft as having been auto-opened, preventing repeated forced navigations on app launch.
    /// </summary>
    public virtual void MarkDraftAsAutoOpened(string userId, string channelId)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(channelId)) return;
        _autoOpenedDrafts[(userId, channelId)] = true;
    }

    /// <summary>
    /// Records the user's currently focused channel.
    /// </summary>
    public virtual void UpdateActiveChannel(string userId, string channelId)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(channelId)) return;
        _activeChannels[userId] = channelId;
    }

    /// <summary>
    /// Retrieves the current draft for a channel, checking memory first then the database repository.
    /// </summary>
    public virtual string? GetDraft(string userId, string channelId)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(channelId)) return null;

        if (_inMemoryDrafts.TryGetValue((userId, channelId), out var memoryDraft) && !string.IsNullOrWhiteSpace(memoryDraft))
        {
            return memoryDraft;
        }

        var diskDraft = _db.ClientStates.GetDraft(userId, channelId);
        if (!string.IsNullOrWhiteSpace(diskDraft))
        {
            _inMemoryDrafts[(userId, channelId)] = diskDraft;
            return diskDraft;
        }

        return null;
    }

    /// <summary>
    private bool IsChannelExisting(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return false;
        if (_db?.ChatChannels != null)
        {
            return _db.ChatChannels.GetById(channelId) != null;
        }
        return true;
    }

    /// <summary>
    /// Checks if the user has an active channel with an unsent draft.
    /// Used during mobile startup to decide whether to auto-redirect to the channel.
    /// Returns null if no draft exists.
    /// </summary>
    public virtual string? GetActiveDraftChannel(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        // 1. Check in-memory drafts for active channel
        if (_activeChannels.TryGetValue(userId, out var activeChannelId) && !string.IsNullOrEmpty(activeChannelId))
        {
            if (_inMemoryDrafts.TryGetValue((userId, activeChannelId), out var draft) && !string.IsNullOrWhiteSpace(draft))
            {
                if (!_autoOpenedDrafts.ContainsKey((userId, activeChannelId)) && IsChannelExisting(activeChannelId))
                {
                    return activeChannelId;
                }
            }
        }

        // 2. Check any other in-memory draft for this user
        var anyMem = _inMemoryDrafts.FirstOrDefault(kvp => kvp.Key.UserId == userId && !string.IsNullOrWhiteSpace(kvp.Value) && !_autoOpenedDrafts.ContainsKey(kvp.Key) && IsChannelExisting(kvp.Key.ChannelId));
        if (!string.IsNullOrEmpty(anyMem.Key.ChannelId))
        {
            return anyMem.Key.ChannelId;
        }

        // 3. Check persisted state in repository
        var state = _db?.ClientStates?.GetByUserId(userId);
        if (state != null)
        {
            if (!string.IsNullOrEmpty(state.ActiveChannelId) &&
                state.ChannelDrafts.TryGetValue(state.ActiveChannelId, out var diskDraft) &&
                !string.IsNullOrWhiteSpace(diskDraft) &&
                !_autoOpenedDrafts.ContainsKey((userId, state.ActiveChannelId)) &&
                IsChannelExisting(state.ActiveChannelId))
            {
                return state.ActiveChannelId;
            }

            var anyDisk = state.ChannelDrafts.FirstOrDefault(kvp => !string.IsNullOrWhiteSpace(kvp.Value) && !_autoOpenedDrafts.ContainsKey((userId, kvp.Key)) && IsChannelExisting(kvp.Key));
            if (!string.IsNullOrEmpty(anyDisk.Key))
            {
                return anyDisk.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// Registers that the user is actively typing in a channel.
    /// </summary>
    public virtual void SetTyping(string userId, string channelId, bool isTyping)
    {
        if (string.IsNullOrEmpty(userId)) return;

        if (isTyping && !string.IsNullOrEmpty(channelId))
        {
            _activeTypingChannels[userId] = channelId;
        }
        else
        {
            _activeTypingChannels.TryRemove(userId, out _);
        }
    }

    /// <summary>
    /// Immediately stops the typing indicator for this user across SignalR and local events.
    /// </summary>
    public virtual void StopActiveTyping(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;

        if (_activeTypingChannels.TryRemove(userId, out var channelId) && !string.IsNullOrEmpty(channelId))
        {
            _ = StopTypingInternalAsync(userId, channelId);
        }
    }

    private async Task StopTypingInternalAsync(string userId, string channelId)
    {
        try
        {
            _chatState.NotifyUserStoppedTyping(channelId, userId);
            await _hubContext.Clients.Group($"channel_{channelId}").SendAsync("UserStoppedTyping", new
            {
                ChannelId = channelId,
                UserId = userId
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast UserStoppedTyping for user {UserId} in channel {ChannelId}", userId, channelId);
        }
    }

    /// <summary>
    /// Flushes any unsent in-memory draft to disk for this user, and cancels active typing.
    /// Called strictly on app exit triggers (unfocus, circuit down, push unfocus beacon).
    /// </summary>
    public virtual void FlushToDiskIfUnsent(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;

        StopActiveTyping(userId);

        var userDrafts = _inMemoryDrafts
            .Where(kvp => kvp.Key.UserId == userId && !string.IsNullOrWhiteSpace(kvp.Value))
            .ToList();

        var state = _db.ClientStates.GetByUserId(userId);
        
        // If there are no drafts in memory AND no state on disk, nothing to do.
        if (userDrafts.Count == 0 && state == null)
        {
            return;
        }

        // We have something to update. Ensure state exists.
        state ??= new UserClientState { Id = userId, UserId = userId };

        // Update ActiveChannelId if we have one in memory
        if (_activeChannels.TryGetValue(userId, out var activeChan) && !string.IsNullOrEmpty(activeChan))
        {
            state.ActiveChannelId = activeChan;
        }

        // Only update the drafts we actually have in memory.
        foreach (var draftEntry in userDrafts)
        {
            state.ChannelDrafts[draftEntry.Key.ChannelId] = draftEntry.Value;
        }

        state.LastUpdatedAt = DateTime.UtcNow;
        _db.ClientStates.Save(state);
        
        if (userDrafts.Count > 0)
        {
            _logger.LogInformation("Flushed unsent drafts for user {UserId} to disk on app exit.", userId);
        }
    }

    /// <summary>
    /// Clears the draft both from memory and from disk when the message has been sent.
    /// </summary>
    public virtual void ClearDraft(string userId, string channelId)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(channelId)) return;

        _inMemoryDrafts.TryRemove((userId, channelId), out _);
        _autoOpenedDrafts.TryRemove((userId, channelId), out _);
        _db.ClientStates.ClearDraft(userId, channelId);
        StopActiveTyping(userId);
    }
}
