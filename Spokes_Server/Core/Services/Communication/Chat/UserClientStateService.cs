namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Communication;

/// <summary>
/// Singleton service managing transient in-memory client state:
/// RAM-only chat drafts, active channel tracking, and active typing indicators.
/// </summary>
public class UserClientStateService : IUserClientStateService
{
    private readonly Func<ChatChannelRepository?>? _channelRepoAccessor;
    private readonly ChatStateService _chatState;
    private readonly ILogger<UserClientStateService> _logger;

    // In-memory drafts: Key: (UserId, ChannelId), Value: Draft message
    private readonly ConcurrentDictionary<(string UserId, string ChannelId), string> _inMemoryDrafts = new();

    // Active channel per user: Key: UserId, Value: ChannelId
    private readonly ConcurrentDictionary<string, string> _activeChannels = new();

    // Active typing channel per user: Key: UserId, Value: ChannelId
    private readonly ConcurrentDictionary<string, string> _activeTypingChannels = new();

    // Tracks channels that have already been auto-opened for a user, to prevent repeated forced navigation. Key: (UserId, ChannelId)
    private readonly ConcurrentDictionary<(string UserId, string ChannelId), bool> _autoOpenedDrafts = new();

    public UserClientStateService(
        ChatChannelRepository? channels,
        ChatStateService chatState,
        ILogger<UserClientStateService> logger)
        : this(() => channels, chatState, logger)
    {
    }

    public UserClientStateService(
        Func<ChatChannelRepository?> channelRepoAccessor,
        ChatStateService chatState,
        ILogger<UserClientStateService> logger)
    {
        _channelRepoAccessor = channelRepoAccessor;
        _chatState = chatState;
        _logger = logger;
    }

    [ActivatorUtilitiesConstructor]
    public UserClientStateService(
        CommunicationContext communicationContext,
        ChatStateService chatState,
        ILogger<UserClientStateService> logger)
        : this(() => communicationContext.ChatChannels, chatState, logger)
    {
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
    /// Retrieves the current draft for a channel from RAM only. Drafts are never persisted to disk.
    /// </summary>
    public virtual string? GetDraft(string userId, string channelId)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(channelId)) return null;

        if (_inMemoryDrafts.TryGetValue((userId, channelId), out var memoryDraft) && !string.IsNullOrWhiteSpace(memoryDraft))
        {
            return memoryDraft;
        }

        return null;
    }

    private bool IsChannelExisting(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return false;
        var channels = _channelRepoAccessor?.Invoke();
        if (channels != null)
        {
            return channels.GetById(channelId) != null;
        }
        return true;
    }

    /// <summary>
    /// Checks if the user has an active channel with an unsent draft in RAM.
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
    /// Immediately stops the typing indicator for this user across local circuit subscribers.
    /// </summary>
    public virtual void StopActiveTyping(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;

        if (_activeTypingChannels.TryRemove(userId, out var channelId) && !string.IsNullOrEmpty(channelId))
        {
            try
            {
                _chatState.NotifyUserStoppedTyping(channelId, userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify UserStoppedTyping for user {UserId} in channel {ChannelId}", userId, channelId);
            }
        }
    }

    /// <summary>
    /// Clears the in-memory draft when the message has been sent and stops any active typing indicator.
    /// </summary>
    public virtual void ClearDraft(string userId, string channelId)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(channelId)) return;

        _inMemoryDrafts.TryRemove((userId, channelId), out _);
        _autoOpenedDrafts.TryRemove((userId, channelId), out _);
        StopActiveTyping(userId);
    }
}
