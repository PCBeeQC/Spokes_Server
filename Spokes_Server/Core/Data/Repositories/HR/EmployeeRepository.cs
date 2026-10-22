using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class EmployeeRepository : JsonRepository<Employee>
{
    public EmployeeRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public EmployeeRepository(DiskPersistenceService writer, IConfiguration config, ILogger<EmployeeRepository>? logger)
        // FIX: Tell the base class to ONLY look for "profile.json" files
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Employees"), "profile.json", logger)
    {
    }

    protected override string GetFilePath(Employee item) =>
        Path.Combine(_basePath, item.Id, "profile.json");

    /// <summary>
    /// Gets all active, non-system, non-suspended, non-banned employees eligible for assignment and selection.
    /// </summary>
    public virtual List<Employee> GetActiveEmployees() => GetAll().Where(e => e.IsSelectable).ToList();

    public override void LoadFromDisk()
    {
        base.LoadFromDisk();

        foreach (var emp in _cache.Values)
        {
            if (!string.IsNullOrEmpty(emp.AvatarBase64))
            {
                try
                {
                    if (DataUriHelper.TryParse(emp.AvatarBase64, out var bytes, out _))
                    {
                        var filePath = Path.Combine(_basePath, emp.Id, "avatar.png");
                        File.WriteAllBytes(filePath, bytes);
                        emp.AvatarFile = "avatar.png";
                        emp.AvatarBase64 = null;
                        Save(emp); // Persist changes
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[EmployeeRepository] Failed to migrate avatar for {EmployeeId}", emp.Id);
                }
            }
        }
    }
}
