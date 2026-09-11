using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using System.Collections.Concurrent;
using System.Globalization;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Core;

public class TimerService
{
    private readonly TimesheetRepository _timesheets;
    private readonly EmployeeRepository _employees;
    private readonly ConcurrentDictionary<string, TimerState> _timers = new();
    private readonly string _dataPath;

    public event Action? OnTimerChanged;

    public TimerService(TimesheetRepository timesheets, EmployeeRepository employees, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _timesheets = timesheets;
        _employees = employees;
        _dataPath = config["DataPath"] ?? "Data";
        RestoreTimerStates();
    }

    public TimerState GetState(string employeeId)
    {
        return _timers.GetOrAdd(employeeId, id => new TimerState { EmployeeId = id });
    }

    public void Start(string employeeId, string projectId, string workTypeId, string? subTaskId, string note = "")
    {
        var state = GetState(employeeId);
        state.ProjectId = projectId;
        state.WorkTypeId = workTypeId;
        state.SubTaskId = subTaskId;
        state.Note = note;
        state.StartedAt = DateTime.UtcNow;
        state.IsRunning = true;
        PersistTimerState(state);
        OnTimerChanged?.Invoke();
    }

    public TimeSpan Stop(string employeeId)
    {
        var state = GetState(employeeId);
        if (!state.IsRunning || state.StartedAt == null)
            return TimeSpan.Zero;

        var elapsed = DateTime.UtcNow - state.StartedAt.Value;
        var hours = Math.Round((decimal)elapsed.TotalHours, 2);

        // Only log if at least 1 minute
        if (hours >= 0.02m && !string.IsNullOrEmpty(state.ProjectId) && !string.IsNullOrEmpty(state.WorkTypeId))
        {
            LogTimeEntry(employeeId, state.ProjectId, state.WorkTypeId, state.SubTaskId, hours, state.Note);
        }

        state.IsRunning = false;
        state.StartedAt = null;
        state.ProjectId = null;
        state.WorkTypeId = null;
        state.SubTaskId = null;
        state.Note = string.Empty;
        DeleteTimerState(employeeId);
        OnTimerChanged?.Invoke();

        return elapsed;
    }

    public void Cancel(string employeeId)
    {
        var state = GetState(employeeId);
        state.IsRunning = false;
        state.StartedAt = null;
        state.ProjectId = null;
        state.WorkTypeId = null;
        state.SubTaskId = null;
        state.Note = string.Empty;
        DeleteTimerState(employeeId);
        OnTimerChanged?.Invoke();
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
        if (timesheet.Status == "Submitted" || timesheet.Status == "Approved")
        {
            Console.WriteLine($"[TimerService] Skipping log for {employeeId} — timesheet is {timesheet.Status}");
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
        try
        {
            var path = GetTimerStatePath(state.EmployeeId);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimerService] Failed to persist timer state: {ex.Message}");
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
            Console.WriteLine($"[TimerService] Failed to delete timer state: {ex.Message}");
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
                        Console.WriteLine($"[TimerService] Restored running timer for employee {state.EmployeeId}");
                    }
                    else
                    {
                        // Clean up invalid state files
                        File.Delete(timerFile);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TimerService] Failed to restore timer from {timerFile}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimerService] Failed to scan for persisted timers: {ex.Message}");
        }
    }
}



