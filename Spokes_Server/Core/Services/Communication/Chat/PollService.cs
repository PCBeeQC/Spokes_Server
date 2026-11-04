using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class PollService
{
    private readonly ChatPollRepository _polls;
    private readonly ChatChannelRepository _channels;
    private readonly EmployeeRepository _employees;
    private readonly IChatAuthorizationService _chatAuth;
    private readonly IChatRealtimeGateway? _realtimeGateway;
    private readonly ChatStateService _chatState;
    private readonly ICryptoService _crypto;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PollService>? _logger;
    private readonly ConcurrentDictionary<string, object> _pollLocks = new();

    private object GetPollLock(string pollId) => _pollLocks.GetOrAdd(pollId, _ => new object());

    public PollService(
        CommunicationContext communicationContext,
        HrContext hrContext,
        ProjectsContext? projectsContext = null,
        IChatAuthorizationService? chatAuth = null,
        IChatRealtimeGateway? realtimeGateway = null,
        ChatStateService? chatState = null,
        ICryptoService? crypto = null,
        TimeProvider? timeProvider = null,
        ILogger<PollService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(communicationContext);
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(communicationContext.ChatPolls);

        _polls = communicationContext.ChatPolls;
        _channels = communicationContext.ChatChannels;
        _employees = hrContext.Employees;
        _chatAuth = chatAuth ?? new ChatAuthorizationService(
            _channels,
            projectsContext?.Projects!,
            hrContext.Teams,
            _employees);
        _realtimeGateway = realtimeGateway;
        _chatState = chatState ?? new ChatStateService();
        _crypto = crypto ?? new CryptoService();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public ChatPoll? GetPollById(string pollId, string requestingUserId, string? expectedChannelId = null)
    {
        if (string.IsNullOrWhiteSpace(pollId) || string.IsNullOrWhiteSpace(requestingUserId))
        {
            return null;
        }

        var poll = _polls.GetById(pollId);
        if (poll == null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(expectedChannelId) &&
            !string.Equals(poll.ChannelId, expectedChannelId, StringComparison.Ordinal))
        {
            return null;
        }

        var employee = _employees.GetById(requestingUserId);
        var channel = _channels.GetById(poll.ChannelId);
        if (employee == null || channel == null || !_chatAuth.CanUserAccessChannel(channel, employee))
        {
            return null;
        }

        return ClonePoll(poll);
    }

    public List<ChatPoll> GetPollsForChannel(string channelId, string requestingUserId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(requestingUserId))
        {
            return [];
        }

        var employee = _employees.GetById(requestingUserId);
        var channel = _channels.GetById(channelId);
        if (employee == null || channel == null || !_chatAuth.CanUserAccessChannel(channel, employee))
        {
            return [];
        }

        return _polls.GetByChannel(channelId).Select(ClonePoll).ToList();
    }

    public bool CanUserModifyPoll(ChatPoll? poll, Employee? user)
    {
        if (poll == null || user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return false;
        }

        var channel = _channels.GetById(poll.ChannelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, user))
        {
            return false;
        }

        return poll.CreatorId == user.Id || user.IsAdmin || user.HasPermission(AppPermissions.Chat.Moderator);
    }

    private static bool TryValidateEncryptedPollKey(
        ChatChannel channel,
        Employee actor,
        string? plainChannelKey,
        out string errorMessage,
        out string errorCode)
    {
        if (string.IsNullOrEmpty(plainChannelKey))
        {
            errorMessage = "Vault is locked. Channel key is required for encrypted polls.";
            errorCode = "VAULT_LOCKED";
            return false;
        }

        var keys = channel.EncryptedChannelKeys;
        bool hasKeyAccess = keys != null &&
            (keys.ContainsKey(actor.Id) ||
             (!actor.HasVaultPassword && keys.ContainsKey("ServerMaster")));

        if (!hasKeyAccess)
        {
            errorMessage = "User does not hold an encryption key for this channel.";
            errorCode = "KEY_NOT_FOUND";
            return false;
        }

        errorMessage = string.Empty;
        errorCode = string.Empty;
        return true;
    }

    public ChatPoll DecryptPollForDisplay(ChatPoll poll, string? plainChannelKey)
    {
        ArgumentNullException.ThrowIfNull(poll);

        var copy = ClonePoll(poll);
        if (!copy.IsEncrypted || string.IsNullOrEmpty(plainChannelKey))
        {
            return copy;
        }

        try
        {
            copy.Question = _crypto.DecryptAes(copy.Question, plainChannelKey);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to decrypt poll question for poll {PollId}", copy.Id);
        }

        foreach (var opt in copy.Options)
        {
            try
            {
                opt.Text = _crypto.DecryptAes(opt.Text, plainChannelKey);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to decrypt poll option {OptionId} for poll {PollId}", opt.Id, copy.Id);
            }
        }

        return copy;
    }

    public SpokesResult<ChatPoll> CreatePoll(ChatPoll poll, string creatorId, string? plainChannelKey = null)
    {
        if (poll == null)
        {
            return SpokesResult<ChatPoll>.Failure("Poll cannot be null.", "INVALID_ARGUMENT");
        }

        if (string.IsNullOrWhiteSpace(creatorId))
        {
            return SpokesResult<ChatPoll>.Failure("Creator ID is required.", "INVALID_ARGUMENT");
        }

        var creator = _employees.GetById(creatorId);
        if (creator == null)
        {
            return SpokesResult<ChatPoll>.Failure($"User '{creatorId}' not found.", "NOT_FOUND");
        }

        if (!creator.IsActive || creator.IsSuspended || creator.IsBanned)
        {
            return SpokesResult<ChatPoll>.Failure("User is not authorized to post a poll.", "FORBIDDEN");
        }

        var channel = _channels.GetById(poll.ChannelId);
        if (channel == null)
        {
            return SpokesResult<ChatPoll>.Failure($"Channel '{poll.ChannelId}' not found.", "NOT_FOUND");
        }

        if (!_chatAuth.CanUserAccessChannel(channel, creator) || !_chatAuth.CanUserPostToChannel(channel, creator))
        {
            return SpokesResult<ChatPoll>.Failure("User is not authorized to post a poll in this channel.", "FORBIDDEN");
        }

        if (channel.IsEncrypted && !TryValidateEncryptedPollKey(channel, creator, plainChannelKey, out var encError, out var encCode))
        {
            return SpokesResult<ChatPoll>.Failure(encError, encCode);
        }

        var cleanQuestion = poll.Question?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanQuestion))
        {
            return SpokesResult<ChatPoll>.Failure("Poll question is required.", "INVALID_ARGUMENT");
        }

        var validOptions = (poll.Options ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.Text))
            .Select(o => new ChatPollOption
            {
                Id = string.IsNullOrWhiteSpace(o.Id) ? Guid.NewGuid().ToString() : o.Id,
                Text = o.Text.Trim(),
                Emoji = string.IsNullOrWhiteSpace(o.Emoji) ? null : o.Emoji.Trim(),
                AddedByUserId = poll.IsAnonymous ? null : creatorId,
                AnonymousVoteCount = 0,
                VoterIds = []
            })
            .ToList();

        if (validOptions.Count < 2)
        {
            return SpokesResult<ChatPoll>.Failure("A poll must have at least 2 options.", "INVALID_ARGUMENT");
        }

        poll.Id = string.IsNullOrWhiteSpace(poll.Id) ? Guid.NewGuid().ToString() : poll.Id;
        poll.CreatorId = creatorId;
        poll.CreatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        poll.Question = cleanQuestion;
        poll.Options = validOptions;
        poll.IsEncrypted = false;

        if (channel.IsEncrypted)
        {
            try
            {
                poll.Question = _crypto.EncryptAes(poll.Question, plainChannelKey!);
                foreach (var opt in poll.Options)
                {
                    opt.Text = _crypto.EncryptAes(opt.Text, plainChannelKey!);
                }
                poll.IsEncrypted = true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to encrypt poll {PollId} for channel {ChannelId}", poll.Id, channel.Id);
                return SpokesResult<ChatPoll>.Failure($"Failed to encrypt poll: {ex.Message}", "CRYPTO_FAILED");
            }
        }

        _polls.Save(poll);
        BroadcastPollUpdated(poll);

        return SpokesResult<ChatPoll>.Success(poll);
    }

    public SpokesResult<ChatPoll> UpdatePoll(ChatPoll updatedPoll, string userId, string? plainChannelKey = null)
    {
        if (updatedPoll == null || string.IsNullOrWhiteSpace(updatedPoll.Id))
        {
            return SpokesResult<ChatPoll>.Failure("Valid poll is required.", "INVALID_ARGUMENT");
        }

        var user = _employees.GetById(userId);
        if (user == null)
        {
            return SpokesResult<ChatPoll>.Failure($"User '{userId}' not found.", "NOT_FOUND");
        }

        if (!user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return SpokesResult<ChatPoll>.Failure("User is not authorized to edit this poll.", "FORBIDDEN");
        }

        var existing = _polls.GetById(updatedPoll.Id);
        if (existing == null)
        {
            return SpokesResult<ChatPoll>.Failure($"Poll '{updatedPoll.Id}' not found.", "NOT_FOUND");
        }

        var channel = _channels.GetById(existing.ChannelId);
        if (channel == null)
        {
            return SpokesResult<ChatPoll>.Failure($"Channel '{existing.ChannelId}' not found.", "NOT_FOUND");
        }

        if (!CanUserModifyPoll(existing, user))
        {
            return SpokesResult<ChatPoll>.Failure("User is not authorized to edit this poll.", "FORBIDDEN");
        }

        bool requiresEncryption = channel.IsEncrypted || existing.IsEncrypted;
        if (requiresEncryption && !TryValidateEncryptedPollKey(channel, user, plainChannelKey, out var updEncError, out var updEncCode))
        {
            return SpokesResult<ChatPoll>.Failure(updEncError, updEncCode);
        }

        var cleanQuestion = updatedPoll.Question?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanQuestion))
        {
            return SpokesResult<ChatPoll>.Failure("Poll question is required.", "INVALID_ARGUMENT");
        }

        var incomingOptions = (updatedPoll.Options ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.Text))
            .ToList();

        if (incomingOptions.Count < 2)
        {
            return SpokesResult<ChatPoll>.Failure("A poll must have at least 2 options.", "INVALID_ARGUMENT");
        }

        lock (GetPollLock(existing.Id))
        {
            existing = _polls.GetById(existing.Id) ?? existing;

            if (existing.IsAnonymous && !updatedPoll.IsAnonymous)
            {
                return SpokesResult<ChatPoll>.Failure("Anonymous voting cannot be disabled once enabled.", "INVALID_OPERATION");
            }

            var isAnonymous = existing.IsAnonymous || updatedPoll.IsAnonymous;
            var existingOptionsById = existing.Options.ToDictionary(o => o.Id, o => o);
            var mergedOptions = new List<ChatPollOption>();

            try
            {
                foreach (var incoming in incomingOptions)
                {
                    var optId = string.IsNullOrWhiteSpace(incoming.Id) ? Guid.NewGuid().ToString() : incoming.Id;
                    existingOptionsById.TryGetValue(optId, out var previousOpt);

                    var optText = incoming.Text.Trim();
                    if (requiresEncryption)
                    {
                        optText = _crypto.EncryptAes(optText, plainChannelKey!);
                    }

                    if (isAnonymous)
                    {
                        mergedOptions.Add(new ChatPollOption
                        {
                            Id = optId,
                            Text = optText,
                            Emoji = string.IsNullOrWhiteSpace(incoming.Emoji) ? null : incoming.Emoji.Trim(),
                            AddedByUserId = null,
                            AnonymousVoteCount = previousOpt != null ? Math.Max(0, previousOpt.VoteCount) : 0,
                            VoterIds = []
                        });
                    }
                    else
                    {
                        mergedOptions.Add(new ChatPollOption
                        {
                            Id = optId,
                            Text = optText,
                            Emoji = string.IsNullOrWhiteSpace(incoming.Emoji) ? null : incoming.Emoji.Trim(),
                            AddedByUserId = previousOpt != null
                                ? (string.IsNullOrWhiteSpace(previousOpt.AddedByUserId) ? existing.CreatorId : previousOpt.AddedByUserId)
                                : userId,
                            AnonymousVoteCount = 0,
                            VoterIds = previousOpt != null
                                ? previousOpt.VoterIds.Distinct().ToList()
                                : []
                        });
                    }
                }

                // If switching from multi-choice to single-choice on a non-anonymous poll, keep only the first voted option per user
                if (!isAnonymous && !updatedPoll.AllowMultipleAnswers)
                {
                    var seenVoters = new HashSet<string>();
                    foreach (var opt in mergedOptions)
                    {
                        opt.VoterIds.RemoveAll(voterId => !seenVoters.Add(voterId));
                    }
                }

                if (requiresEncryption)
                {
                    existing.Question = _crypto.EncryptAes(cleanQuestion, plainChannelKey!);
                    existing.IsEncrypted = true;
                }
                else
                {
                    existing.Question = cleanQuestion;
                    existing.IsEncrypted = false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to encrypt updated poll {PollId} for channel {ChannelId}", existing.Id, channel.Id);
                return SpokesResult<ChatPoll>.Failure($"Failed to encrypt poll: {ex.Message}", "CRYPTO_FAILED");
            }

            existing.Options = mergedOptions;
            existing.AllowMultipleAnswers = updatedPoll.AllowMultipleAnswers;
            existing.AllowAddOptions = updatedPoll.AllowAddOptions;
            existing.IsAnonymous = isAnonymous;
            existing.ExpiresAt = updatedPoll.ExpiresAt;
            existing.IsClosed = updatedPoll.IsClosed;

            _polls.Save(existing);
            BroadcastPollUpdated(existing);
            return SpokesResult<ChatPoll>.Success(existing);
        }
    }

    public SpokesResult<ChatPoll> ToggleVote(string pollId, string optionId, string userId)
    {
        if (string.IsNullOrWhiteSpace(pollId) || string.IsNullOrWhiteSpace(optionId) || string.IsNullOrWhiteSpace(userId))
        {
            return SpokesResult<ChatPoll>.Failure("Poll ID, option ID, and user ID are required.", "INVALID_ARGUMENT");
        }

        var user = _employees.GetById(userId);
        if (user == null)
        {
            return SpokesResult<ChatPoll>.Failure($"User '{userId}' not found.", "NOT_FOUND");
        }

        if (!user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return SpokesResult<ChatPoll>.Failure("User is not authorized to vote.", "FORBIDDEN");
        }

        var poll = _polls.GetById(pollId);
        if (poll == null)
        {
            return SpokesResult<ChatPoll>.Failure($"Poll '{pollId}' not found.", "NOT_FOUND");
        }

        var channel = _channels.GetById(poll.ChannelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, user))
        {
            return SpokesResult<ChatPoll>.Failure("User is not authorized to vote in this channel.", "FORBIDDEN");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (GetPollLock(pollId))
        {
            poll = _polls.GetById(pollId) ?? poll;
            if (poll.IsExpired(now))
            {
                return SpokesResult<ChatPoll>.Failure("This poll has ended.", "POLL_CLOSED");
            }

            var targetOption = poll.Options.FirstOrDefault(o => o.Id == optionId);
            if (targetOption == null)
            {
                return SpokesResult<ChatPoll>.Failure($"Option '{optionId}' not found.", "NOT_FOUND");
            }

            if (poll.IsAnonymous)
            {
                // Anonymous polls never record who voted for what in RAM or on disk.
                foreach (var opt in poll.Options)
                {
                    opt.VoterIds.Clear();
                }
                targetOption.AnonymousVoteCount++;
            }
            else if (targetOption.VoterIds.Contains(userId))
            {
                targetOption.VoterIds.Remove(userId);
            }
            else
            {
                if (!poll.AllowMultipleAnswers)
                {
                    foreach (var opt in poll.Options)
                    {
                        opt.VoterIds.Remove(userId);
                    }
                }
                targetOption.VoterIds.Add(userId);
            }

            _polls.Save(poll);
            BroadcastPollUpdated(poll);
            return SpokesResult<ChatPoll>.Success(poll);
        }
    }

    public SpokesResult<ChatPoll> AddParticipantOption(string pollId, string userId, string optionText, string? emoji = null, string? plainChannelKey = null)
    {
        if (string.IsNullOrWhiteSpace(pollId) || string.IsNullOrWhiteSpace(userId))
        {
            return SpokesResult<ChatPoll>.Failure("Poll ID and user ID are required.", "INVALID_ARGUMENT");
        }

        var cleanText = optionText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanText))
        {
            return SpokesResult<ChatPoll>.Failure("Option text cannot be empty.", "INVALID_ARGUMENT");
        }

        var user = _employees.GetById(userId);
        if (user == null)
        {
            return SpokesResult<ChatPoll>.Failure($"User '{userId}' not found.", "NOT_FOUND");
        }

        if (!user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return SpokesResult<ChatPoll>.Failure("User is not authorized to add options.", "FORBIDDEN");
        }

        var poll = _polls.GetById(pollId);
        if (poll == null)
        {
            return SpokesResult<ChatPoll>.Failure($"Poll '{pollId}' not found.", "NOT_FOUND");
        }

        var channel = _channels.GetById(poll.ChannelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, user) || !_chatAuth.CanUserPostToChannel(channel, user))
        {
            return SpokesResult<ChatPoll>.Failure("User is not authorized to add options in this channel.", "FORBIDDEN");
        }

        bool requiresEncryption = channel.IsEncrypted || poll.IsEncrypted;
        if (requiresEncryption && !TryValidateEncryptedPollKey(channel, user, plainChannelKey, out var optEncError, out var optEncCode))
        {
            return SpokesResult<ChatPoll>.Failure(optEncError, optEncCode);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (GetPollLock(pollId))
        {
            poll = _polls.GetById(pollId) ?? poll;
            if (poll.IsExpired(now))
            {
                return SpokesResult<ChatPoll>.Failure("This poll has ended.", "POLL_CLOSED");
            }

            if (!poll.AllowAddOptions && !CanUserModifyPoll(poll, user))
            {
                return SpokesResult<ChatPoll>.Failure("Adding options is disabled for this poll.", "FORBIDDEN");
            }

            if (poll.Options.Count >= 20)
            {
                return SpokesResult<ChatPoll>.Failure("Maximum number of poll options reached.", "LIMIT_EXCEEDED");
            }

            var storedText = cleanText;
            if (requiresEncryption)
            {
                try
                {
                    storedText = _crypto.EncryptAes(cleanText, plainChannelKey!);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to encrypt participant poll option for poll {PollId}", pollId);
                    return SpokesResult<ChatPoll>.Failure($"Failed to encrypt poll option: {ex.Message}", "CRYPTO_FAILED");
                }
            }

            if (!poll.IsAnonymous && !poll.AllowMultipleAnswers)
            {
                foreach (var opt in poll.Options)
                {
                    opt.VoterIds.Remove(userId);
                }
            }

            var newOption = new ChatPollOption
            {
                Id = Guid.NewGuid().ToString(),
                Text = storedText,
                Emoji = string.IsNullOrWhiteSpace(emoji) ? null : emoji.Trim(),
                AddedByUserId = poll.IsAnonymous ? null : userId,
                AnonymousVoteCount = poll.IsAnonymous ? 1 : 0,
                VoterIds = poll.IsAnonymous ? [] : [userId]
            };

            poll.Options.Add(newOption);
            _polls.Save(poll);
            BroadcastPollUpdated(poll);
            return SpokesResult<ChatPoll>.Success(poll);
        }
    }

    public SpokesResult<ChatPoll> SetPollClosed(string pollId, string userId, bool isClosed)
    {
        var user = _employees.GetById(userId);
        if (user == null)
        {
            return SpokesResult<ChatPoll>.Failure($"User '{userId}' not found.", "NOT_FOUND");
        }

        var poll = _polls.GetById(pollId);
        if (poll == null)
        {
            return SpokesResult<ChatPoll>.Failure($"Poll '{pollId}' not found.", "NOT_FOUND");
        }

        if (!CanUserModifyPoll(poll, user))
        {
            return SpokesResult<ChatPoll>.Failure("User is not authorized to close or reopen this poll.", "FORBIDDEN");
        }

        lock (GetPollLock(pollId))
        {
            poll = _polls.GetById(pollId) ?? poll;
            poll.IsClosed = isClosed;
            if (!isClosed && poll.ExpiresAt.HasValue && poll.ExpiresAt.Value <= _timeProvider.GetUtcNow().UtcDateTime)
            {
                poll.ExpiresAt = null;
            }
            _polls.Save(poll);
            BroadcastPollUpdated(poll);
            return SpokesResult<ChatPoll>.Success(poll);
        }
    }

    public SpokesResult DeletePoll(string pollId, string userId)
    {
        var user = _employees.GetById(userId);
        if (user == null)
        {
            return SpokesResult.Failure($"User '{userId}' not found.", "NOT_FOUND");
        }

        var poll = _polls.GetById(pollId);
        if (poll == null)
        {
            return SpokesResult.Failure($"Poll '{pollId}' not found.", "NOT_FOUND");
        }

        if (!CanUserModifyPoll(poll, user))
        {
            return SpokesResult.Failure("User is not authorized to delete this poll.", "FORBIDDEN");
        }

        var channelId = poll.ChannelId;
        _polls.Delete(pollId);
        _pollLocks.TryRemove(pollId, out _);
        _chatState.NotifyPollDeleted(pollId);

        if (_realtimeGateway != null && !string.IsNullOrEmpty(channelId))
        {
            _ = _realtimeGateway.SendToChannelAsync(channelId, "PollDeleted", new
            {
                PollId = pollId,
                ChannelId = channelId
            });
        }

        return SpokesResult.Success();
    }

    public static ChatPoll ClonePoll(ChatPoll poll)
    {
        return new ChatPoll
        {
            Id = poll.Id,
            ChannelId = poll.ChannelId,
            CreatorId = poll.CreatorId,
            Question = poll.Question,
            AllowMultipleAnswers = poll.AllowMultipleAnswers,
            AllowAddOptions = poll.AllowAddOptions,
            IsAnonymous = poll.IsAnonymous,
            IsClosed = poll.IsClosed,
            IsEncrypted = poll.IsEncrypted,
            CreatedAt = poll.CreatedAt,
            ExpiresAt = poll.ExpiresAt,
            Options = poll.Options.Select(o => new ChatPollOption
            {
                Id = o.Id,
                Text = o.Text,
                Emoji = o.Emoji,
                AddedByUserId = poll.IsAnonymous ? null : o.AddedByUserId,
                AnonymousVoteCount = o.AnonymousVoteCount,
                VoterIds = poll.IsAnonymous ? [] : o.VoterIds.ToList()
            }).ToList()
        };
    }

    private void BroadcastPollUpdated(ChatPoll poll)
    {
        var snapshot = ClonePoll(poll);
        _chatState.NotifyPollUpdated(snapshot);

        if (_realtimeGateway != null && !string.IsNullOrEmpty(snapshot.ChannelId))
        {
            _ = _realtimeGateway.SendToChannelAsync(snapshot.ChannelId, "PollUpdated", snapshot);
        }
    }
}
