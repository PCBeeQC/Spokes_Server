using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.HR;

public class TimesheetTimerService
{
    private readonly TimesheetRepository _timesheets;
    private readonly EmployeeRepository _employees;
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
        : this(timesheets, employees, config, (ILogger?)logger)
    {
    }

    public TimesheetTimerService(
        TimesheetRepository timesheets,
        EmployeeRepository employees,
        IConfiguration config,
        ILogger? logger)
    {
        _timesheets = timesheets;
        _employees = employees;
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
            var today = DateTime.Today;
            var weekNum = ISOWeek.GetWeekOfYear(today);
            var year = ISOWeek.GetYear(today);

            var timesheet = _timesheets.GetAll()
                .FirstOrDefault(t => t.EmployeeId == employeeId && t.Year == year && t.WeekNumber == weekNum);

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

    public SpokesResult<TimeSpan> Stop(string employeeId)
    {
        lock (GetLock(employeeId))
        {
            var state = _timers.GetOrAdd(employeeId, id => new TimerState { EmployeeId = id });
            if (!state.IsRunning || state.StartedAt == null)
                return SpokesResult<TimeSpan>.Success(TimeSpan.Zero);

            var elapsed = DateTime.UtcNow - state.StartedAt.Value;
            var hours = Math.Round((decimal)elapsed.TotalHours, 2);

            var projectId = state.ProjectId;
            var workTypeId = state.WorkTypeId;
            var subTaskId = state.SubTaskId;
            var note = state.Note;

            var today = DateTime.Today;
            var weekNum = ISOWeek.GetWeekOfYear(today);
            var year = ISOWeek.GetYear(today);

            var timesheet = _timesheets.GetAll()
                .FirstOrDefault(t => t.EmployeeId == employeeId && t.Year == year && t.WeekNumber == weekNum);

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

            state.IsRunning = false;
            state.StartedAt = null;
            state.ProjectId = null;
            state.WorkTypeId = null;
            state.SubTaskId = null;
            state.Note = string.Empty;
            DeleteTimerState(employeeId);

            // Only log if at least 1 minute
            if (hours >= 0.02m && !string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(workTypeId))
            {
                LogTimeEntry(employeeId, projectId, workTypeId, subTaskId, hours, note);
            }

            OnTimerChanged?.Invoke();
            return SpokesResult<TimeSpan>.Success(elapsed);
        }
    }

    public void Cancel(string employeeId)
    {
        lock (GetLock(employeeId))
        {
            var state = _timers.GetOrAdd(employeeId, id => new TimerState { EmployeeId = id });
            state.IsRunning = false;
            state.StartedAt = null;
            state.ProjectId = null;
            state.WorkTypeId = null;
            state.SubTaskId = null;
            state.Note = string.Empty;
            DeleteTimerState(employeeId);
            OnTimerChanged?.Invoke();
        }
    }

    private void LogTimeEntry(string employeeId, string projectId, string workTypeId, string? subTaskId, decimal hours, string note)
    {
        var today = DateTime.Today;
        var weekNum = ISOWeek.GetWeekOfYear(today);
        var year = ISOWeek.GetYear(today);

        // Find or create the timesheet for this week
        var timesheet = _timesheets.GetAll()
            .FirstOrDefault(t => t.EmployeeId == employeeId && t.Year == year && t.WeekNumber == weekNum);

        if (timesheet == null)
        {
            var employee = _employees.GetById(employeeId);
            timesheet = new Timesheet
            {
                EmployeeId = employeeId,
                EmployeeName = employee?.FullName ?? "",
                Year = year,
                WeekNumber = weekNum
            };
        }

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
