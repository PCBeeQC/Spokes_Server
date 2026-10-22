using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
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
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ChatStateService? _chatStateService;
    private readonly ILogger<CalendarService>? _logger;

    public event Action<CalendarEvent>? EventUpdated;

    public CalendarService(
        CalendarEventRepository calendarEvents,
        EmployeeRepository employees,
        CompanyProfileRepository companyProfiles,
        ChatStateService? chatStateService = null,
        ILogger<CalendarService>? logger = null)
    {
        _calendarEvents = calendarEvents;
        _employees = employees;
        _companyProfiles = companyProfiles;
        _chatStateService = chatStateService;
        _logger = logger;
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

        var evt = _calendarEvents.GetById(eventId);
        if (evt == null)
            return SpokesResult.Failure("Event not found.");

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

        var employee = _employees.GetById(employeeId);
        if (employee == null)
            return SpokesResult.Failure("Employee not found.");

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
        _employees.Save(employee);

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
        var serverProfile = _companyProfiles.Get();
        if (string.IsNullOrEmpty(employeeId))
        {
            return CalendarCategoryDefaults.CloneList(serverProfile?.CalendarCategories);
        }

        var employee = _employees.GetById(employeeId);
        return employee != null
            ? employee.EnsureCalendarCategories(serverProfile)
            : CalendarCategoryDefaults.CloneList(serverProfile?.CalendarCategories);
    }
}
