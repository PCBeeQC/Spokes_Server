using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class EmployeeRepository : JsonRepository<Employee>
{
    public EmployeeRepository(DiskPersistenceService writer, IConfiguration config)
        // FIX: Tell the base class to ONLY look for "profile.json" files
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Employees"), "profile.json")
    {
    }

    protected override string GetFilePath(Employee item) =>
        Path.Combine(_basePath, item.Id, "profile.json");

    /// <summary>
    /// Gets all active, non-system, non-suspended, non-banned employees eligible for assignment and selection.
    /// </summary>
    public List<Employee> GetActiveEmployees() => GetAll().Where(e => e.IsSelectable).ToList();

    public override void LoadFromDisk()
    {
        base.LoadFromDisk();

        foreach (var emp in _cache.Values)
        {
            if (!string.IsNullOrEmpty(emp.AvatarBase64))
            {
                try
                {
                    var base64Data = emp.AvatarBase64;
                    if (base64Data.Contains(','))
                    {
                        base64Data = base64Data[(base64Data.IndexOf(',') + 1)..];
                    }
                    var bytes = Convert.FromBase64String(base64Data);
                    var filePath = Path.Combine(_basePath, emp.Id, "avatar.png");
                    File.WriteAllBytes(filePath, bytes);
                    emp.AvatarFile = "avatar.png";
                    emp.AvatarBase64 = null;
                    Save(emp); // Persist changes
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EmployeeRepository] Failed to migrate avatar for {emp.Id}: {ex.Message}");
                }
            }
        }
    }
}
