namespace Spokes_Server.Core.Helpers;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Models.Communication;

/// <summary>
/// Helper class for calculating channel-aware read receipts for both
/// 1-on-1 Direct Messages and multi-user group channels.
/// </summary>
public static class ChatReadReceiptHelper
{
    /// <summary>
    /// Computes latest read receipts (inline avatars) and cumulative read receipts (dialog)
    /// based on channel type (1-on-1 Direct Message vs Group / Team / Project channel).
    /// </summary>
    public static (Dictionary<string, List<string>> LatestReceipts, Dictionary<string, List<string>> CumulativeReceipts) 
        CalculateReadReceipts(ChatChannel? channel, IEnumerable<ChatMessage>? messages)
    {
        var latestReceipts = new Dictionary<string, List<string>>();
        var cumulativeReceipts = new Dictionary<string, List<string>>();

        if (messages == null)
            return (latestReceipts, cumulativeReceipts);

        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        if (messageList.Count == 0)
            return (latestReceipts, cumulativeReceipts);

        // Determine if this channel is a 1-on-1 Direct Message
        var distinctParticipants = channel?.ParticipantIds?
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList() ?? new List<string>();

        if (distinctParticipants.Count < 2)
        {
            var senders = messageList.Select(m => m.SenderId).Where(id => !string.IsNullOrEmpty(id)).Distinct();
            foreach (var s in senders)
            {
                if (!distinctParticipants.Contains(s))
                    distinctParticipants.Add(s);
            }
        }

        bool isDirect1on1 = channel != null 
            && (channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == "Direct")
            && distinctParticipants.Count == 2;

        if (isDirect1on1)
        {
            var userA = distinctParticipants[0];
            var userB = distinctParticipants[1];

            bool HasUserSeen(string userId, ChatMessage m)
            {
                if (m.SenderId == userId) return true;
                if (m.ReadBy != null && m.ReadBy.Contains(userId)) return true;
                return messageList.Any(other => other.SenderId == userId && other.SentAt >= m.SentAt);
            }

            // Latest message sent by userA that userB has seen
            var latestSentByASeenByB = messageList
                .Where(m => m.SenderId == userA && HasUserSeen(userB, m))
                .OrderByDescending(m => m.SentAt)
                .FirstOrDefault();

            if (latestSentByASeenByB != null)
            {
                if (!latestReceipts.ContainsKey(latestSentByASeenByB.Id))
                    latestReceipts[latestSentByASeenByB.Id] = new List<string>();
                latestReceipts[latestSentByASeenByB.Id].Add(userB);
            }

            // Latest message sent by userB that userA has seen
            var latestSentByBSeenByA = messageList
                .Where(m => m.SenderId == userB && HasUserSeen(userA, m))
                .OrderByDescending(m => m.SentAt)
                .FirstOrDefault();

            if (latestSentByBSeenByA != null)
            {
                if (!latestReceipts.ContainsKey(latestSentByBSeenByA.Id))
                    latestReceipts[latestSentByBSeenByA.Id] = new List<string>();
                latestReceipts[latestSentByBSeenByA.Id].Add(userA);
            }

            // Cumulative read receipts for 1:1 DMs
            foreach (var msg in messageList)
            {
                var cumulativeReaders = new List<string>();
                var otherUser = msg.SenderId == userA ? userB : userA;
                if (HasUserSeen(otherUser, msg))
                {
                    cumulativeReaders.Add(otherUser);
                }
                if (cumulativeReaders.Any())
                {
                    cumulativeReceipts[msg.Id] = cumulativeReaders;
                }
            }
        }
        else
        {
            // Multi-user cursor tracking for group / team / project channels
            var latestReadPerUser = new Dictionary<string, ChatMessage>();

            foreach (var msg in messageList)
            {
                if (!string.IsNullOrEmpty(msg.SenderId))
                {
                    if (!latestReadPerUser.TryGetValue(msg.SenderId, out var currentLatest) || msg.SentAt > currentLatest.SentAt)
                    {
                        latestReadPerUser[msg.SenderId] = msg;
                    }
                }

                if (msg.ReadBy != null)
                {
                    foreach (var userId in msg.ReadBy)
                    {
                        if (!latestReadPerUser.TryGetValue(userId, out var currentLatest) || msg.SentAt > currentLatest.SentAt)
                        {
                            latestReadPerUser[userId] = msg;
                        }
                    }
                }
            }

            // Invert to map MessageId -> List<UserId> (for inline avatars)
            foreach (var kvp in latestReadPerUser)
            {
                var userId = kvp.Key;
                var latestMsg = kvp.Value;

                // In group channels, a user's avatar should not be displayed in their own message's read receipt
                if (userId == latestMsg.SenderId)
                    continue;

                if (!latestReceipts.ContainsKey(latestMsg.Id))
                {
                    latestReceipts[latestMsg.Id] = new List<string>();
                }

                latestReceipts[latestMsg.Id].Add(userId);
            }

            // Compute cumulative readers for "View Read Receipts" dialog
            foreach (var msg in messageList)
            {
                var cumulativeReaders = new List<string>();
                foreach (var kvp in latestReadPerUser)
                {
                    var userId = kvp.Key;
                    var latestMsg = kvp.Value;

                    if (userId == msg.SenderId)
                        continue;

                    if (latestMsg.SentAt >= msg.SentAt)
                    {
                        cumulativeReaders.Add(userId);
                    }
                }
                if (cumulativeReaders.Any())
                {
                    cumulativeReceipts[msg.Id] = cumulativeReaders;
                }
            }
        }

        return (latestReceipts, cumulativeReceipts);
    }
}
