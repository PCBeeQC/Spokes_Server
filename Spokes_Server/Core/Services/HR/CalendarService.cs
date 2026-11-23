using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;

namespace Spokes_Server.Core.Services.HR;

/// <summary>
/// Domain service for managing calendar events, attendance, and category configurations.
/// </summary>
public class CalendarService
{
    private readonly CalendarEventRepository _calendarEvents;
    private readonly EmployeeRepository? _employees;
    private readonly CompanyProfileRepository? _companyProfiles;
    private readonly TeamRepository? _teams;
    private readonly ChatStateService? _chatStateService;
    private readonly NotificationQueueService? _notificationQueue;

    public event Action<CalendarEvent>? EventUpdated;

    public CalendarService(
        CalendarEventRepository calendarEvents,
        EmployeeRepository? employees = null,
        CompanyProfileRepository? companyProfiles = null,
        ChatStateService? chatStateService = null,
        TeamRepository? teams = null,
        NotificationQueueService? notificationQueue = null)
    {
        _calendarEvents = calendarEvents;
        _employees = employees;
        _companyProfiles = companyProfiles;
        _chatStateService = chatStateService;
        _teams = teams;
        _notificationQueue = notificationQueue;
    }

    public static bool HasCalendarViewAccess(Employee? user) =>
        user != null && user.IsActive && !user.IsBanned && user.HasEffectivePermission(AppPermissions.Calendar.View, allowSuspended: true);

    public static bool CanCreateOrParticipateInCalendar(Employee? user) =>
        user != null && user.IsActiveUnsuspended && user.HasEffectivePermission(AppPermissions.Calendar.View, allowSuspended: false);

    public static bool HasCalendarAdminOverride(Employee? user) =>
        user != null && user.IsActiveUnsuspended && (
            user.IsEffectiveAdmin
            || user.HasEffectivePermission(AppPermissions.Admin.ManageSettings, allowSuspended: false)
            || user.HasEffectivePermission(AppPermissions.Admin.ManageUsers, allowSuspended: false));

    public virtual HashSet<string> GetUserTeamIds(Employee? user)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (user == null) return set;

        if (!string.IsNullOrEmpty(user.TeamId))
        {
            set.Add(user.TeamId);
        }

        if (_teams != null && !string.IsNullOrEmpty(user.Id))
        {
            foreach (var team in _teams.GetAll().Where(t => string.Equals(t.LeaderId, user.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrEmpty(team.Id))
                {
                    set.Add(team.Id);
                }
            }
        }

        return set;
    }

    public virtual bool CanUserViewEvent(CalendarEvent? evt, Employee? user, IEnumerable<string>? userTeamIds = null)
    {
        var teams = userTeamIds != null
            ? new HashSet<string>(userTeamIds, StringComparer.OrdinalIgnoreCase)
            : GetUserTeamIds(user);
        return CanUserViewEventCore(evt, user, teams);
    }

    private static bool MatchesEventVisibility(CalendarEvent evt, string userId, IReadOnlySet<string>? userTeamIds) =>
        evt.IsCompanyWide
        || (!string.IsNullOrEmpty(evt.EmployeeId) && string.Equals(evt.EmployeeId, userId, StringComparison.OrdinalIgnoreCase))
        || (evt.Attendees != null && evt.Attendees.Any(a => string.Equals(a, userId, StringComparison.OrdinalIgnoreCase)))
        || (userTeamIds != null && userTeamIds.Count > 0 && evt.InvitedTeamIds != null && evt.InvitedTeamIds.Any(tId => userTeamIds.Contains(tId)));

    public static bool CanUserViewEventCore(CalendarEvent? evt, Employee? user, IReadOnlySet<string>? userTeamIds = null)
    {
        if (evt == null || user == null)
            return false;

        if (!HasCalendarViewAccess(user) && !HasCalendarAdminOverride(user))
            return false;

        if (HasCalendarAdminOverride(user))
            return true;

        return MatchesEventVisibility(evt, user.Id, userTeamIds);
    }

    public virtual bool CanUserEditEvent(CalendarEvent? evt, Employee? user)
    {
        if (evt == null || user == null)
            return false;

        if (HasCalendarAdminOverride(user))
            return true;

        if (!CanCreateOrParticipateInCalendar(user))
            return false;

        return !string.IsNullOrEmpty(evt.EmployeeId)
            && string.Equals(evt.EmployeeId, user.Id, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolveAuthorizedParticipant(string userId, out Employee employee, out SpokesResult? failure, string deniedMessage, bool allowNullEmployeeRepo = false)
    {
        if (allowNullEmployeeRepo && _employees == null)
        {
            employee = null!;
            failure = null;
            return true;
        }

        var resolved = _employees?.GetById(userId);
        if (resolved == null || (!CanCreateOrParticipateInCalendar(resolved) && !HasCalendarAdminOverride(resolved)))
        {
            employee = null!;
            failure = SpokesResult.Failure(deniedMessage, "FORBIDDEN");
            return false;
        }

        employee = resolved;
        failure = null;
        return true;
    }

    private bool TryGetAuthorizedEvent(
        string eventId,
        string userId,
        string deniedMessage,
        bool requireViewAccess,
        out CalendarEvent evt,
        out Employee employee,
        out SpokesResult? failure)
    {
        evt = null!;
        employee = null!;

        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(userId))
        {
            failure = SpokesResult.Failure("Event ID and user ID are required.");
            return false;
        }

        if (!TryResolveAuthorizedParticipant(userId, out employee, out failure, deniedMessage, allowNullEmployeeRepo: !requireViewAccess))
        {
            return false;
        }

        var loaded = _calendarEvents.GetById(eventId);
        if (loaded == null)
        {
            failure = SpokesResult.Failure(requireViewAccess ? "Event not found." : "Event not found or has no attendees.");
            return false;
        }

        if (requireViewAccess && !CanUserViewEvent(loaded, employee))
        {
            failure = SpokesResult.Failure("You do not have access to this event.");
            return false;
        }

        evt = loaded;
        failure = null;
        return true;
    }

    public virtual SpokesResult UpdateUserEventNotificationPreference(
        string eventId,
        string userId,
        List<CalendarReminder>? personalReminders = null,
        bool? receiveEventReminders = null,
        bool? isMuted = null)
    {
        if (!TryGetAuthorizedEvent(
                eventId,
                userId,
                "User does not have permission to update calendar event preferences.",
                requireViewAccess: true,
                out var evt,
                out var resolvedEmployee,
                out var failure))
        {
            return failure!;
        }

        evt.UserReminderPreferences ??= new();
        if (!evt.UserReminderPreferences.TryGetValue(userId, out var pref) || pref == null)
        {
            pref = new CalendarUserReminderPreference
            {
                ReceiveEventReminders = !resolvedEmployee.MuteSharedCalendarAlertsByDefault
            };
            evt.UserReminderPreferences[userId] = pref;
        }

        if (personalReminders != null)
        {
            pref.PersonalReminders = personalReminders.ConvertAll(r => r.Clone());
        }
        if (receiveEventReminders.HasValue)
        {
            pref.ReceiveEventReminders = receiveEventReminders.Value;
        }
        if (isMuted.HasValue)
        {
            pref.IsMuted = isMuted.Value;
        }

        _calendarEvents.Save(evt);
        EventUpdated?.Invoke(evt);
        _chatStateService?.NotifyCalendarEventUpdated(evt);
        return SpokesResult.Success();
    }

    public virtual List<CalendarEvent> GetEventsForUser(string userId, IEnumerable<string>? teamIds = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new List<CalendarEvent>();

        var employee = _employees?.GetById(userId);
        if (_employees != null && (employee == null || (!HasCalendarViewAccess(employee) && !HasCalendarAdminOverride(employee))))
            return new List<CalendarEvent>();

        var teamSet = teamIds != null
            ? new HashSet<string>(teamIds, StringComparer.OrdinalIgnoreCase)
            : GetUserTeamIds(employee);

        return _calendarEvents.GetAll()
            .Where(e => MatchesEventVisibility(e, userId, teamSet))
            .ToList();
    }

    public virtual CalendarEvent? GetEventById(string eventId) =>
        string.IsNullOrEmpty(eventId) ? null : _calendarEvents.GetById(eventId);

    private Employee ResolveActingEmployeeOrThrow(string? actingUserId)
    {
        if (string.IsNullOrWhiteSpace(actingUserId))
        {
            throw new UnauthorizedAccessException("Acting user ID is required.");
        }

        return _employees!.GetById(actingUserId)
            ?? throw new UnauthorizedAccessException("User not found.");
    }

    public virtual void SaveEvent(CalendarEvent evt, string? actingUserId = null)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var existing = !string.IsNullOrEmpty(evt.Id) ? _calendarEvents.GetById(evt.Id) : null;

        if (_employees != null)
        {
            var actor = ResolveActingEmployeeOrThrow(actingUserId);

            if (existing == null)
            {
                if (!CanCreateOrParticipateInCalendar(actor) && !HasCalendarAdminOverride(actor))
                    throw new UnauthorizedAccessException("User does not have permission to create calendar events.");

                if (!HasCalendarAdminOverride(actor)
                    && !string.IsNullOrEmpty(evt.EmployeeId)
                    && !string.Equals(evt.EmployeeId, actor.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Users cannot create calendar events for another user.");
                }

                if (string.IsNullOrEmpty(evt.EmployeeId))
                {
                    evt.EmployeeId = actor.Id;
                }
            }
            else
            {
                if (!CanUserEditEvent(existing, actor))
                    throw new UnauthorizedAccessException("User is not authorized to modify this calendar event.");

                if (!HasCalendarAdminOverride(actor)
                    && !string.Equals(evt.EmployeeId, existing.EmployeeId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Users cannot reassign event ownership.");
                }
            }

            bool isBecomingCompanyWide = evt.IsCompanyWide && (existing == null || !existing.IsCompanyWide);
            bool allowNonAdminsCompanyWide = _companyProfiles?.Get()?.Privacy?.AllowNonAdminsCompanyWideCalendar ?? true;
            if (isBecomingCompanyWide && !allowNonAdminsCompanyWide && !HasCalendarAdminOverride(actor))
            {
                throw new UnauthorizedAccessException("Only administrators can create company-wide calendar events.");
            }
        }

        _calendarEvents.Save(evt);
        EventUpdated?.Invoke(evt);
        _chatStateService?.NotifyCalendarEventUpdated(evt);
        if (existing != null)
        {
            _notificationQueue?.PurgeForCalendarEvent(evt.Id);
        }
    }

    public virtual void DeleteEvent(string eventId, string? actingUserId = null)
    {
        if (string.IsNullOrEmpty(eventId))
            return;

        if (_employees != null)
        {
            var actor = ResolveActingEmployeeOrThrow(actingUserId);

            if (!CanCreateOrParticipateInCalendar(actor) && !HasCalendarAdminOverride(actor))
                throw new UnauthorizedAccessException("User does not have permission to delete calendar events.");

            var existing = _calendarEvents.GetById(eventId);
            if (existing == null)
                return;

            if (!CanUserEditEvent(existing, actor))
                throw new UnauthorizedAccessException("User is not authorized to delete this calendar event.");
        }

        _calendarEvents.Delete(eventId);
        _chatStateService?.NotifyCalendarEventDeleted(eventId);
        _notificationQueue?.PurgeForCalendarEvent(eventId);
    }

    public virtual SpokesResult RsvpToEvent(string eventId, string userId)
    {
        if (!TryGetAuthorizedEvent(eventId, userId, "User does not have permission to RSVP to calendar events.", requireViewAccess: true, out var evt, out _, out var failure))
            return failure!;

        evt.Attendees ??= new List<string>();
        if (!evt.Attendees.Contains(userId))
        {
            evt.Attendees.Add(userId);
            _calendarEvents.Save(evt);
            EventUpdated?.Invoke(evt);
            _chatStateService?.NotifyCalendarEventUpdated(evt);
            return SpokesResult.Success();
        }

        return SpokesResult.Failure("User is already attending this event.");
    }

    public virtual SpokesResult CancelRsvpEvent(string eventId, string userId)
    {
        if (!TryGetAuthorizedEvent(eventId, userId, "User does not have permission to modify calendar attendance.", requireViewAccess: false, out var evt, out _, out var failure))
            return failure!;

        if (evt.Attendees == null)
            return SpokesResult.Failure("Event not found or has no attendees.");

        if (evt.Attendees.Remove(userId))
        {
            _calendarEvents.Save(evt);
            EventUpdated?.Invoke(evt);
            _chatStateService?.NotifyCalendarEventUpdated(evt);
            _notificationQueue?.PurgeForCalendarEvent(eventId, userId);
            return SpokesResult.Success();
        }

        return SpokesResult.Failure("User was not attending this event.");
    }

    public virtual SpokesResult UpdateUserCategories(string employeeId, List<CalendarCategory> categories)
    {
        if (string.IsNullOrEmpty(employeeId))
            return SpokesResult.Failure("Employee ID is required.");

        var employee = _employees?.GetById(employeeId);
        if (employee == null)
            return SpokesResult.Failure("Employee not found.");

        if (!CanCreateOrParticipateInCalendar(employee) && !HasCalendarAdminOverride(employee))
            return SpokesResult.Failure("User does not have permission to update calendar categories.", "FORBIDDEN");

        categories ??= [];
        foreach (var c in categories)
        {
            c.Name = (c.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                return SpokesResult.Failure("All categories must have a name.");
            }
        }

        employee.CalendarCategories = categories;
        _employees?.Save(employee);

        // Cascade category updates to existing user events
        var catDict = categories.ToDictionary(c => c.Id, c => c);
        var userEvents = _calendarEvents.GetAll().Where(e => e.EmployeeId == employeeId).ToList();
        foreach (var evt in userEvents)
        {
            if (!string.IsNullOrEmpty(evt.CategoryId) && catDict.TryGetValue(evt.CategoryId, out var newCat))
            {
                if (evt.Category != newCat.Name || evt.CategoryColor != newCat.Color)
                {
                    evt.Category = newCat.Name;
                    evt.CategoryColor = newCat.Color;
                    _calendarEvents.Save(evt);
                    _chatStateService?.NotifyCalendarEventUpdated(evt);
                }
            }
        }

        return SpokesResult.Success();
    }

    public virtual List<CalendarCategory> GetEffectiveCategories(string? employeeId)
    {
        var serverProfile = _companyProfiles?.Get();
        if (string.IsNullOrEmpty(employeeId))
        {
            return CalendarCategoryDefaults.CloneList(serverProfile?.CalendarCategories);
        }

        var employee = _employees?.GetById(employeeId);
        return employee != null
            ? employee.EnsureCalendarCategories(serverProfile)
            : CalendarCategoryDefaults.CloneList(serverProfile?.CalendarCategories);
    }
}
