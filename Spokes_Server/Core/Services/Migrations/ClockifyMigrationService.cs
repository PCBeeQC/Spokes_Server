using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.Migrations;

public class ClockifyRecord
{
    public string Project { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string StartDateString { get; set; } = string.Empty;
    public decimal? DurationDecimal { get; set; }
    public string Billable { get; set; } = "Yes";
}

public class MigrationParsedFile
{
    public string FileName { get; set; } = string.Empty;
    public List<ClockifyRecord> Records { get; set; } = [];
    public DateTime? MinDate { get; set; }
    public DateTime? MaxDate { get; set; }

    public string DateRangeDisplay
    {
        get
        {
            if (MinDate is not { } min || MaxDate is not { } max) return "Unknown";
            if (min.Year == max.Year) return min.Year.ToString();
            return $"{min.Year} - {max.Year}";
        }
    }
}

public class ClockifyUserMap
{
    public string ClockifyEmail { get; set; } = string.Empty;
    public string ClockifyName { get; set; } = string.Empty;
    
    // Matched or assigned Spokes Employee ID
    public string? SpokesEmployeeId { get; set; }
    
    // True if user elected to create a new placeholder employee
    public bool CreateNew { get; set; }
}

public class ClockifyProjectMap
{
    public string ExtractedCode { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    
    public string? SpokesProjectId { get; set; }
    public bool IsNew { get; set; }
}

public class ClockifyMigrationService
{
    private readonly HrContext _hr;
    private readonly ProjectsContext _projects;
    private readonly ILogger<ClockifyMigrationService>? _logger;

    [ActivatorUtilitiesConstructor]
    public ClockifyMigrationService(HrContext hr, ProjectsContext projects, ILogger<ClockifyMigrationService>? logger = null)
    {
        _hr = hr;
        _projects = projects;
        _logger = logger;
    }

    public async Task<MigrationParsedFile> ParseCsvAsync(Stream fileStream, string fileName)
    {
        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms);
        ms.Position = 0;

        return await Task.Run(() =>
        {
            using var reader = new StreamReader(ms);
            var rows = CsvReaderHelper.Parse(reader);

            var records = new List<ClockifyRecord>();
            if (rows.Count > 0)
            {
                var headerRow = rows[0];
                var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headerRow.Count; i++)
                {
                    var h = headerRow[i].Trim();
                    if (!string.IsNullOrEmpty(h))
                    {
                        headerMap[h] = i;
                    }
                }

                string GetField(List<string> row, params string[] possibleHeaders)
                {
                    foreach (var header in possibleHeaders)
                    {
                        if (headerMap.TryGetValue(header, out var index) && index < row.Count)
                        {
                            return row[index].Trim();
                        }
                    }
                    return string.Empty;
                }

                for (int r = 1; r < rows.Count; r++)
                {
                    var row = rows[r];
                    if (row.Count == 0 || (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])))
                    {
                        continue;
                    }

                    var billable = GetField(row, "Billable");
                    if (string.IsNullOrWhiteSpace(billable))
                    {
                        billable = "Yes";
                    }

                    decimal? durationDecimal = null;
                    var durationStr = GetField(row, "Duration (decimal)", "Duration (Decimal)", "Duration");
                    if (!string.IsNullOrWhiteSpace(durationStr))
                    {
                        if (decimal.TryParse(durationStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDuration))
                        {
                            durationDecimal = parsedDuration;
                        }
                        else if (decimal.TryParse(durationStr, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsedDurationLocal))
                        {
                            durationDecimal = parsedDurationLocal;
                        }
                    }

                    var record = new ClockifyRecord
                    {
                        Project = GetField(row, "Project"),
                        Description = GetField(row, "Description"),
                        Task = GetField(row, "Task"),
                        User = GetField(row, "User"),
                        Email = GetField(row, "Email"),
                        StartDateString = GetField(row, "Start Date", "StartDate", "Date"),
                        DurationDecimal = durationDecimal,
                        Billable = billable
                    };

                    records.Add(record);
                }
            }

            var parsed = new MigrationParsedFile
            {
                FileName = fileName,
                Records = records
            };

            var dates = records
                .Select(r => ParseClockifyDate(r.StartDateString))
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .ToList();

            if (dates.Count > 0)
            {
                parsed.MinDate = dates.Min();
                parsed.MaxDate = dates.Max();
            }

            return parsed;
        });
    }

    private DateTime? ParseClockifyDate(string dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;
        if (DateTime.TryParse(dateStr, out var d)) return d;
        return null;
    }

    public List<ClockifyUserMap> MapUsers(List<ClockifyRecord> allRecords)
    {
        var uniqueUsers = allRecords
            .GroupBy(r => new { Email = r.Email?.Trim().ToLower() ?? "", Name = r.User?.Trim() ?? "" })
            .Select(g => new ClockifyUserMap
            {
                ClockifyEmail = g.Key.Email,
                ClockifyName = g.Key.Name
            })
            .Where(u => !string.IsNullOrEmpty(u.ClockifyEmail) || !string.IsNullOrEmpty(u.ClockifyName))
            .ToList();

        var employees = _hr.Employees.GetAll();

        foreach (var u in uniqueUsers)
        {
            // 1. Try exact email match
            if (!string.IsNullOrEmpty(u.ClockifyEmail))
            {
                var match = employees.FirstOrDefault(e => e.Email.Equals(u.ClockifyEmail, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    u.SpokesEmployeeId = match.Id;
                    continue;
                }
            }
            
            // 2. Try Name match
            if (!string.IsNullOrEmpty(u.ClockifyName))
            {
                var match = employees.FirstOrDefault(e => e.FullName.Equals(u.ClockifyName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    u.SpokesEmployeeId = match.Id;
                }
            }
        }

        return uniqueUsers;
    }

    public List<ClockifyProjectMap> MapProjects(List<ClockifyRecord> allRecords)
    {
        var uniqueProjects = allRecords
            .Where(r => !string.IsNullOrWhiteSpace(r.Project))
            .Select(r => r.Project.Trim())
            .Distinct()
            .ToList();

        var map = new List<ClockifyProjectMap>();
        var spokesProjects = _projects.Projects.GetAll();
        var regex = new Regex(@"(?i)[A-Z]{3}\d{3}");

        foreach (var cp in uniqueProjects)
        {
            var match = regex.Match(cp);
            var extractedCode = match.Success ? match.Value.ToUpper() : string.Empty;

            var mapping = new ClockifyProjectMap
            {
                OriginalName = cp,
                ExtractedCode = extractedCode
            };

            if (!string.IsNullOrEmpty(extractedCode))
            {
                var existing = spokesProjects.FirstOrDefault(p => 
                    p.ProjectNumber.Equals(extractedCode, StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Contains(extractedCode, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    mapping.SpokesProjectId = existing.Id;
                }
                else
                {
                    mapping.IsNew = true;
                }
            }
            else
            {
                // No code found, let's try exact name match
                var existing = spokesProjects.FirstOrDefault(p => p.Name.Equals(cp, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    mapping.SpokesProjectId = existing.Id;
                }
                else
                {
                    mapping.IsNew = true;
                }
            }

            map.Add(mapping);
        }

        return map;
    }

    public async Task ExecuteMigrationAsync(
        List<ClockifyRecord> allRecords, 
        List<ClockifyUserMap> userMaps, 
        List<ClockifyProjectMap> projectMaps,
        Employee executingEmployee,
        CancellationToken cancellationToken = default)
    {
        if (executingEmployee == null || (!executingEmployee.IsAdmin && !executingEmployee.HasPermission(AppPermissions.Admin.ManageSettings)))
        {
            throw new UnauthorizedAccessException("User is not authorized to execute data migration.");
        }

        await Task.Run(() => ExecuteMigrationInternal(allRecords, userMaps, projectMaps, executingEmployee), cancellationToken);
    }

    private void ExecuteMigrationInternal(
        List<ClockifyRecord> allRecords, 
        List<ClockifyUserMap> userMaps, 
        List<ClockifyProjectMap> projectMaps,
        Employee executingEmployee)
    {
        // 1. Create Missing Users
        foreach (var um in userMaps.Where(u => u.CreateNew && string.IsNullOrEmpty(u.SpokesEmployeeId)))
        {
            var parts = um.ClockifyName.Split(' ', 2);
            var emp = new Employee
            {
                FirstName = parts.Length > 0 ? parts[0] : "Unknown",
                LastName = parts.Length > 1 ? parts[1] : "",
                Email = um.ClockifyEmail,
                IsActive = false, // Placeholder user
                TeamId = _hr.Teams.GetAll().FirstOrDefault()?.Id ?? ""
            };
            _hr.Employees.Save(emp);
            um.SpokesEmployeeId = emp.Id;
        }

        // 2. Create Missing Projects
        var newProjectsCache = new Dictionary<string, Project>();
        foreach (var pm in projectMaps.Where(p => p.IsNew))
        {
            var p = new Project
            {
                ProjectNumber = pm.ExtractedCode,
                Name = pm.OriginalName,
                Status = ProjectStatus.Draft,
                Description = "Imported from Clockify"
            };
            _projects.Projects.Save(p);
            pm.SpokesProjectId = p.Id;
            newProjectsCache[p.Id] = p;
        }

        // 3. Generate Timesheets
        var entriesToProcess = allRecords.Where(r => r.DurationDecimal.HasValue && r.DurationDecimal.Value > 0).ToList();

        // Group by User
        foreach (var userGroup in entriesToProcess.GroupBy(r => new { r.Email, r.User }))
        {
            var um = userMaps.FirstOrDefault(m => m.ClockifyEmail == userGroup.Key.Email && m.ClockifyName == userGroup.Key.User);
            if (um == null || string.IsNullOrEmpty(um.SpokesEmployeeId)) continue; // Skip unmatched users

            var employee = _hr.Employees.GetById(um.SpokesEmployeeId);
            if (employee == null) continue;

            // Group by Year and Week
            foreach (var weekGroup in userGroup.GroupBy(r => 
            {
                var dt = ParseClockifyDate(r.StartDateString) ?? DateTime.UtcNow;
                return new { Year = GetIso8601Year(dt), Week = GetIso8601WeekOfYear(dt) };
            }))
            {
                var existingTimesheet = _hr.Timesheets.GetAll().FirstOrDefault(t => 
                    t.EmployeeId == employee.Id && 
                    t.Year == weekGroup.Key.Year && 
                    t.WeekNumber == weekGroup.Key.Week);

                var timesheet = existingTimesheet ?? new Timesheet
                {
                    EmployeeId = employee.Id,
                    EmployeeName = employee.FullName,
                    Year = weekGroup.Key.Year,
                    WeekNumber = weekGroup.Key.Week,
                    Status = TimesheetStatus.Approved, // Default migrated to Approved to avoid cluttering manager queues
                    SubmittedAt = DateTime.UtcNow,
                    ApprovedAt = DateTime.UtcNow,
                    ApprovedBy = executingEmployee.Id
                };

                foreach (var record in weekGroup)
                {
                    var pm = projectMaps.FirstOrDefault(p => p.OriginalName == record.Project?.Trim());
                    var projectId = pm?.SpokesProjectId;
                    if (string.IsNullOrEmpty(projectId)) continue;

                    var hours = record.DurationDecimal ?? 0m;
                    if (hours <= 0m) continue;

                    var date = ParseClockifyDate(record.StartDateString) ?? DateTime.UtcNow.Date;

                    var entry = new TimeEntry
                    {
                        ProjectId = projectId,
                        WorkTypeId = "", // Or default general ID if needed
                        Date = date,
                        Hours = hours,
                        Description = string.IsNullOrWhiteSpace(record.Description) ? record.Task : record.Description,
                        IsBillable = record.Billable?.Equals("Yes", StringComparison.OrdinalIgnoreCase) ?? true
                    };
                    timesheet.Entries.Add(entry);
                }

                if (timesheet.Entries.Any())
                {
                    _hr.Timesheets.Save(timesheet);
                }
            }
        }

        _logger?.LogInformation(
            "Clockify migration completed by employee {UserId} ({UserName}). Created {UserCount} missing users, {ProjectCount} missing projects, and processed timesheets across {RecordCount} records.",
            executingEmployee.Id,
            executingEmployee.FullName,
            userMaps.Count(u => u.CreateNew),
            projectMaps.Count(p => p.IsNew),
            allRecords.Count);
    }

    private static int GetIso8601WeekOfYear(DateTime time)
    {
        DayOfWeek day = CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(time);
        if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday)
        {
            time = time.AddDays(3);
        }
        return CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(time, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
    }

    private static int GetIso8601Year(DateTime time)
    {
        int week = GetIso8601WeekOfYear(time);
        if (week >= 52 && time.Month == 1) return time.Year - 1;
        if (week == 1 && time.Month == 12) return time.Year + 1;
        return time.Year;
    }
}
