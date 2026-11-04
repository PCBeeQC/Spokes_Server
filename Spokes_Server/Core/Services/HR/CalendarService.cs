using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;

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
    private readonly ILogger<CalendarService>? _logger;

    public event Action<CalendarEvent>? EventUpdated;

    public CalendarService(
        CalendarEventRepository calendarEvents,
        EmployeeRepository? employees = null,
        CompanyProfileRepository? companyProfiles = null,
        ChatStateService? chatStateService = null,
        ILogger<CalendarService>? logger = null,
        TeamRepository? teams = null)
    {
        _calendarEvents = calendarEvents;
        _employees = employees;
        _companyProfiles = companyProfiles;
        _chatStateService = chatStateService;
        _logger = logger;
        _teams = teams;
    }

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
        if (evt == null || user == null)
            return false;

        if (user.IsAdmin || evt.IsCompanyWide)
            return true;

        if (!string.IsNullOrEmpty(evt.EmployeeId) && string.Equals(evt.EmployeeId, user.Id, StringComparison.OrdinalIgnoreCase))
            return true;

        if (evt.Attendees != null && evt.Attendees.Any(a => string.Equals(a, user.Id, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (evt.InvitedTeamIds != null && evt.InvitedTeamIds.Count > 0)
        {
            var teams = userTeamIds != null
                ? new HashSet<string>(userTeamIds, StringComparer.OrdinalIgnoreCase)
                : GetUserTeamIds(user);

            if (evt.InvitedTeamIds.Any(tId => teams.Contains(tId)))
                return true;
        }

        // Unowned events with no team restrictions are open
        if (string.IsNullOrEmpty(evt.EmployeeId) && (evt.InvitedTeamIds == null || evt.InvitedTeamIds.Count == 0))
            return true;

        return false;
    }

    public virtual bool CanUserEditEvent(CalendarEvent? evt, Employee? user)
    {
        if (evt == null || user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
            return false;

        return user.IsAdmin
            || (!string.IsNullOrEmpty(evt.EmployeeId) && string.Equals(evt.EmployeeId, user.Id, StringComparison.OrdinalIgnoreCase))
            || user.HasPermission(AppPermissions.Admin.ManageSettings)
            || user.HasPermission(AppPermissions.Admin.ManageUsers);
    }

    public virtual SpokesResult UpdateUserEventNotificationPreference(
        string eventId,
        string userId,
        List<CalendarReminder>? personalReminders = null,
        bool? receiveEventReminders = null,
        bool? isMuted = null)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(userId))
            return SpokesResult.Failure("Event ID and user ID are required.");

        var resolvedEmployee = _employees?.GetById(userId);
        if (resolvedEmployee != null && (!resolvedEmployee.IsActive || resolvedEmployee.IsSuspended || resolvedEmployee.IsBanned))
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");

        var evt = _calendarEvents.GetById(eventId);
        if (evt == null)
            return SpokesResult.Failure("Event not found.");

        var user = resolvedEmployee ?? new Employee { Id = userId };
        if (!CanUserViewEvent(evt, user))
            return SpokesResult.Failure("You do not have access to this event.");

        evt.UserReminderPreferences ??= new();
        if (!evt.UserReminderPreferences.TryGetValue(userId, out var pref) || pref == null)
        {
            pref = new CalendarUserReminderPreference
            {
                ReceiveEventReminders = !user.MuteSharedCalendarAlertsByDefault
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
        var teamSet = teamIds != null ? new HashSet<string>(teamIds) : null;

        return _calendarEvents.GetAll()
            .Where(e => e.IsCompanyWide
                || (!string.IsNullOrEmpty(userId) && e.EmployeeId == userId)
                || (!string.IsNullOrEmpty(userId) && e.Attendees != null && e.Attendees.Contains(userId))
                || (teamSet != null && teamSet.Count > 0 && e.InvitedTeamIds != null && e.InvitedTeamIds.Any(tId => teamSet.Contains(tId))))
            .ToList();
    }

    public virtual CalendarEvent? GetEventById(string eventId) =>
        string.IsNullOrEmpty(eventId) ? null : _calendarEvents.GetById(eventId);

    public virtual void SaveEvent(CalendarEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!string.IsNullOrEmpty(evt.EmployeeId))
        {
            var owner = _employees?.GetById(evt.EmployeeId);
            if (owner != null && (!owner.IsActive || owner.IsSuspended || owner.IsBanned))
            {
                throw new UnauthorizedAccessException("Suspended, banned, or inactive users cannot save calendar events.");
            }
        }
        _calendarEvents.Save(evt);
        EventUpdated?.Invoke(evt);
        _chatStateService?.NotifyCalendarEventUpdated(evt);
    }

    public virtual void DeleteEvent(string eventId)
    {
        if (!string.IsNullOrEmpty(eventId))
        {
            _calendarEvents.Delete(eventId);
            _chatStateService?.NotifyCalendarEventDeleted(eventId);
        }
    }

    public virtual SpokesResult RsvpToEvent(string eventId, string userId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(userId))
            return SpokesResult.Failure("Event ID and user ID are required.");

        var resolvedEmployee = _employees?.GetById(userId);
        if (resolvedEmployee != null && (!resolvedEmployee.IsActive || resolvedEmployee.IsSuspended || resolvedEmployee.IsBanned))
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");

        var evt = _calendarEvents.GetById(eventId);
        if (evt == null)
            return SpokesResult.Failure("Event not found.");

        var user = resolvedEmployee ?? new Employee { Id = userId };
        if (!CanUserViewEvent(evt, user))
            return SpokesResult.Failure("You do not have access to this event.");

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
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(userId))
            return SpokesResult.Failure("Event ID and user ID are required.");

        var resolvedEmployee = _employees?.GetById(userId);
        if (resolvedEmployee != null && (!resolvedEmployee.IsActive || resolvedEmployee.IsSuspended || resolvedEmployee.IsBanned))
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");

        var evt = _calendarEvents.GetById(eventId);
        if (evt == null || evt.Attendees == null)
            return SpokesResult.Failure("Event not found or has no attendees.");

        if (evt.Attendees.Remove(userId))
        {
            _calendarEvents.Save(evt);
            EventUpdated?.Invoke(evt);
            _chatStateService?.NotifyCalendarEventUpdated(evt);
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

        if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");

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
