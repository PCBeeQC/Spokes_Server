using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Singleton in-process event bus broadcasting chat, poll, moderation, profile, and calendar
/// updates directly to active Blazor Server circuits.
/// </summary>
public class ChatStateService
{
    // Events
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string, string, string>? UserTyping; // channelId, userId, userName
    public event Action<string, string>? UserStoppedTyping; // channelId, userId
    public event Action<ChatMessage>? MessageEdited;
    public event Action<string, string>? MessageDeleted; // channelId, messageId
    public event Action<string, string, string, string, string>? ReactionAdded; // channelId, messageId, emoji, userId, userName
    public event Action<string, string, string, string>? ReactionRemoved; // channelId, messageId, emoji, userId
    public event Action<string, string, List<string>>? MessageRead; // channelId, messageId, readBy
    public event Action<ReportedMessage>? ModerationAlertReceived;
    public event Action<ReportedMessage>? ModerationReportUpdated;
    public event Action<Employee>? EmployeeProfileUpdated;
    public event Action<CalendarEvent>? CalendarEventUpdated;
    public event Action<string>? CalendarEventDeleted;
    public event Action<ChatPoll>? PollUpdated;
    public event Action<string>? PollDeleted;
    public event Action<ChatChannel>? ChannelCreated;
    public event Action<ChatChannel>? ChannelUpdated;

    // Methods to trigger events
    public void NotifyCalendarEventUpdated(CalendarEvent calendarEvent) => CalendarEventUpdated?.Invoke(calendarEvent);
    public void NotifyCalendarEventDeleted(string eventId) => CalendarEventDeleted?.Invoke(eventId);
    public void NotifyPollUpdated(ChatPoll poll) => PollUpdated?.Invoke(poll);
    public void NotifyPollDeleted(string pollId) => PollDeleted?.Invoke(pollId);
    public void NotifyMessageReceived(ChatMessage message) => MessageReceived?.Invoke(message);
    public void NotifyModerationAlert(ReportedMessage report) => ModerationAlertReceived?.Invoke(report);
    public void NotifyModerationReportUpdated(ReportedMessage report) => ModerationReportUpdated?.Invoke(report);
    public void NotifyEmployeeProfileUpdated(Employee employee) => EmployeeProfileUpdated?.Invoke(employee);
    public void NotifyUserTyping(string channelId, string userId, string userName) => UserTyping?.Invoke(channelId, userId, userName);
    public void NotifyUserStoppedTyping(string channelId, string userId) => UserStoppedTyping?.Invoke(channelId, userId);
    public void NotifyMessageEdited(ChatMessage message) => MessageEdited?.Invoke(message);
    public void NotifyMessageDeleted(string channelId, string messageId) => MessageDeleted?.Invoke(channelId, messageId);
    public void NotifyReactionAdded(string channelId, string messageId, string emoji, string userId, string userName) => ReactionAdded?.Invoke(channelId, messageId, emoji, userId, userName);
    public void NotifyReactionRemoved(string channelId, string messageId, string emoji, string userId) => ReactionRemoved?.Invoke(channelId, messageId, emoji, userId);
    public void NotifyMessageRead(string channelId, string messageId, List<string> readBy) => MessageRead?.Invoke(channelId, messageId, readBy);
    public void NotifyChannelCreated(ChatChannel channel) => ChannelCreated?.Invoke(channel);
    public void NotifyChannelUpdated(ChatChannel channel) => ChannelUpdated?.Invoke(channel);
}
