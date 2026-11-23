using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Projects;

namespace Spokes_Server.Core.Services.HR;

public class TimesheetTimerService
{
    private readonly TimesheetRepository _timesheets;
    private readonly EmployeeRepository _employees;
    private readonly ProjectRepository? _projects;
    private readonly TeamRepository? _teams;
    private readonly bool _enforcePermissions;
    private readonly ConcurrentDictionary<string, TimerState> _timers = new();
    private readonly ConcurrentDictionary<string, object> _employeeLocks = new();
    private readonly string _dataPath;
    private readonly ILogger? _logger;

    public event Action? OnTimerChanged;

    private object GetLock(string employeeId) => _employeeLocks.GetOrAdd(employeeId, _ => new object());

    public TimesheetTimerService(
        TimesheetRepository timesheets,
        EmployeeRepository employees,
        IConfiguration config,
        ILogger<TimesheetTimerService>? logger = null)
    {
        _timesheets = timesheets;
        _employees = employees;
        _enforcePermissions = false;
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
        RestoreTimerStates();
    }

    [ActivatorUtilitiesConstructor]
    public TimesheetTimerService(
        TimesheetRepository timesheets,
        EmployeeRepository employees,
        ProjectRepository projects,
        TeamRepository teams,
        IConfiguration config,
        ILogger<TimesheetTimerService>? logger = null)
    {
        _timesheets = timesheets;
        _employees = employees;
        _projects = projects;
        _teams = teams;
        _enforcePermissions = true;
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
        RestoreTimerStates();
    }

    public TimerState GetState(string employeeId)
    {
        lock (GetLock(employeeId))
        {
            return _timers.GetOrAdd(employeeId, id => new TimerState { EmployeeId = id });
        }
    }

    public SpokesResult Start(string employeeId, string projectId, string workTypeId, string? subTaskId, string note = "")
    {
        lock (GetLock(employeeId))
        {
            _ = GetTimerStatePath(employeeId);

            if (!TryValidateTimerActor(employeeId, "start", out var emp, out var isAccountDisabled))
            {
                return SpokesResult.Failure(
                    isAccountDisabled
                        ? "Suspended or inactive accounts cannot start timers."
                        : "You do not have permission to log time to this project.",
                    "FORBIDDEN");
            }

            if (_enforcePermissions)
            {
                var project = _projects?.GetById(projectId);
                if (!ProjectAccessHelper.CanLogTimeToProject(emp, project, _teams))
                {
                    _logger?.LogWarning("[TimesheetTimerService] Cannot start timer for {EmployeeId} on project {ProjectId}: unauthorized project access", employeeId, projectId);
                    return SpokesResult.Failure("You do not have permission to log time to this project.", "FORBIDDEN");
                }
            }

            var timesheet = GetCurrentWeekTimesheet(employeeId, out _, out _, out var weekNum);
            if (timesheet != null && (timesheet.Status == TimesheetStatus.Submitted || timesheet.Status == TimesheetStatus.Approved))
            {
                _logger?.LogWarning("[TimesheetTimerService] Cannot start timer for {EmployeeId}: timesheet for week {Week} is {Status}", employeeId, weekNum, timesheet.Status);
                return SpokesResult.Failure($"Cannot start timer: weekly timesheet is already {timesheet.Status.ToLowerInvariant()}.", "TIMESHEET_LOCKED");
            }

            var state = _timers.GetOrAdd(employeeId, id => new TimerState { EmployeeId = id });
            state.ProjectId = projectId;
            state.WorkTypeId = workTypeId;
            state.SubTaskId = subTaskId;
            state.Note = note;
            state.StartedAt = DateTime.UtcNow;
            state.IsRunning = true;
            PersistTimerState(state);
            OnTimerChanged?.Invoke();
            return SpokesResult.Success();
        }
    }

    private Timesheet? GetCurrentWeekTimesheet(string employeeId, out DateTime today, out int year, out int weekNum)
    {
        today = DateTime.Today;
        var currentWeek = ISOWeek.GetWeekOfYear(today);
        var currentYear = ISOWeek.GetYear(today);
        weekNum = currentWeek;
        year = currentYear;
        return _timesheets.GetAll()
            .FirstOrDefault(t => t.EmployeeId == employeeId && t.Year == currentYear && t.WeekNumber == currentWeek);
    }

    private bool TryValidateTimerActor(string employeeId, string action, out Employee? emp, out bool isAccountDisabled)
    {
        emp = _employees.GetById(employeeId);
        isAccountDisabled = emp != null && !emp.IsActiveUnsuspended;
        if (isAccountDisabled ||
            (_enforcePermissions && (emp == null || !emp.HasPermission(AppPermissions.Timesheets.AccessTimesheet))))
        {
            _logger?.LogWarning("[TimesheetTimerService] Cannot {Action} timer for {EmployeeId}: account is suspended, inactive, or lacks timesheet permission", action, employeeId);
            return false;
        }
        return true;
    }

    private void ClearAndDeleteTimerState(TimerState state, string employeeId)
    {
        state.IsRunning = false;
        state.StartedAt = null;
        state.ProjectId = null;
        state.WorkTypeId = null;
        state.SubTaskId = null;
        state.Note = string.Empty;
        DeleteTimerState(employeeId);
    }

    public SpokesResult<TimeSpan> Stop(string employeeId)
    {
        lock (GetLock(employeeId))
        {
            if (!TryValidateTimerActor(employeeId, "stop", out var emp, out _))
            {
                return SpokesResult<TimeSpan>.Failure("Suspended or unauthorized accounts cannot stop and log timer entries.", "FORBIDDEN");
            }

            var state = _timers.GetOrAdd(employeeId, id => new TimerState { EmployeeId = id });
            if (!state.IsRunning || state.StartedAt == null)
                return SpokesResult<TimeSpan>.Success(TimeSpan.Zero);

            if (_enforcePermissions && !string.IsNullOrEmpty(state.ProjectId) && _projects != null &&
                !ProjectAccessHelper.CanLogTimeToProject(emp, _projects.GetById(state.ProjectId), _teams))
            {
                _logger?.LogWarning("[TimesheetTimerService] Cannot stop timer for {EmployeeId} on project {ProjectId}: unauthorized project access", employeeId, state.ProjectId);
                return SpokesResult<TimeSpan>.Failure("You no longer have permission to log time to this project.", "FORBIDDEN");
            }

            var elapsed = DateTime.UtcNow - state.StartedAt.Value;
            var hours = Math.Round((decimal)elapsed.TotalHours, 2);

            var projectId = state.ProjectId;
            var workTypeId = state.WorkTypeId;
            var subTaskId = state.SubTaskId;
            var note = state.Note;

            var timesheet = GetCurrentWeekTimesheet(employeeId, out var today, out var year, out var weekNum);

            // If time would be logged (>= 0.02h with valid project & worktype), check if timesheet is submitted or approved
            if (hours >= 0.02m && !string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(workTypeId))
            {
                if (timesheet != null && (timesheet.Status == TimesheetStatus.Submitted || timesheet.Status == TimesheetStatus.Approved))
                {
                    _logger?.LogWarning("[TimesheetTimerService] Cannot stop timer and log time for {EmployeeId}: timesheet for week {Week} is {Status}", employeeId, weekNum, timesheet.Status);
                    return SpokesResult<TimeSpan>.Failure(
                        $"Cannot log time: timesheet for week {weekNum} is already {timesheet.Status.ToLowerInvariant()}. Unsubmit or reopen your timesheet to log this time.",
                        "TIMESHEET_LOCKED");
                }
            }

            ClearAndDeleteTimerState(state, employeeId);

            // Only log if at least 1 minute
            if (hours >= 0.02m && !string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(workTypeId))
            {
                LogTimeEntry(employeeId, emp, timesheet, today, year, weekNum, projectId, workTypeId, subTaskId, hours, note);
            }

            OnTimerChanged?.Invoke();
            return SpokesResult<TimeSpan>.Success(elapsed);
        }
    }

    public void Cancel(string employeeId)
    {
        lock (GetLock(employeeId))
        {
            if (!TryValidateTimerActor(employeeId, "cancel", out _, out _))
            {
                return;
            }

            var state = _timers.GetOrAdd(employeeId, id => new TimerState { EmployeeId = id });
            ClearAndDeleteTimerState(state, employeeId);
            OnTimerChanged?.Invoke();
        }
    }

    private void LogTimeEntry(
        string employeeId,
        Employee? employee,
        Timesheet? timesheet,
        DateTime today,
        int year,
        int weekNum,
        string projectId,
        string workTypeId,
        string? subTaskId,
        decimal hours,
        string note)
    {
        timesheet ??= new Timesheet
        {
            EmployeeId = employeeId,
            EmployeeName = employee?.FullName ?? "",
            Year = year,
            WeekNumber = weekNum
        };

        // Don't log to submitted/approved timesheets
        if (timesheet.Status == TimesheetStatus.Submitted || timesheet.Status == TimesheetStatus.Approved)
        {
            _logger?.LogInformation("[TimesheetTimerService] Skipping log for {EmployeeId} — timesheet is {Status}", employeeId, timesheet.Status);
            return;
        }

        timesheet.Entries.Add(new TimeEntry
        {
            ProjectId = projectId,
            WorkTypeId = workTypeId,
            SubTaskId = subTaskId,
            Date = today,
            Hours = hours,
            Description = string.IsNullOrWhiteSpace(note) ? "Logged via timer" : note
        });

        _timesheets.Save(timesheet);
    }

    private string GetTimerStatePath(string employeeId)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeId = new string(employeeId.Where(c => !invalidChars.Contains(c) && c != '/' && c != '\\').ToArray());
        if (string.IsNullOrWhiteSpace(safeId))
            throw new ArgumentException("Invalid employee ID", nameof(employeeId));

        var basePath = Path.GetFullPath(Path.Combine(_dataPath, "Employees"));
        var targetPath = Path.GetFullPath(Path.Combine(basePath, safeId, "timer_state.json"));
        if (!targetPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid employee path.");

        return targetPath;
    }

    private void PersistTimerState(TimerState state)
    {
        var path = GetTimerStatePath(state.EmployeeId);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            var tmpPath = $"{path}.tmp.{Guid.NewGuid():N}";
            try
            {
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[TimesheetTimerService] Failed to persist timer state for employee {EmployeeId}", state.EmployeeId);
        }
    }

    private void DeleteTimerState(string employeeId)
    {
        try
        {
            var path = GetTimerStatePath(employeeId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[TimesheetTimerService] Failed to delete timer state for employee {EmployeeId}", employeeId);
        }
    }

    private void RestoreTimerStates()
    {
        try
        {
            var employeesDir = Path.Combine(_dataPath, "Employees");
            if (!Directory.Exists(employeesDir)) return;

            foreach (var empDir in Directory.GetDirectories(employeesDir))
            {
                var timerFile = Path.Combine(empDir, "timer_state.json");
                if (!File.Exists(timerFile)) continue;

                try
                {
                    var json = File.ReadAllText(timerFile);
                    var state = System.Text.Json.JsonSerializer.Deserialize<TimerState>(json);
                    if (state != null && state.IsRunning && state.StartedAt.HasValue)
                    {
                        _timers[state.EmployeeId] = state;
                        _logger?.LogInformation("[TimesheetTimerService] Restored running timer for employee {EmployeeId}", state.EmployeeId);
                    }
                    else
                    {
                        // Clean up invalid state files
                        File.Delete(timerFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[TimesheetTimerService] Failed to restore timer from {TimerFile}", timerFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[TimesheetTimerService] Failed to scan for persisted timers");
        }
    }
}
