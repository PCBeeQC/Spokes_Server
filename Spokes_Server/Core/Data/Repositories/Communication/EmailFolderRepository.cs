using System.Text.Json;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class EmailFolderRepository : JsonRepository<EmailFolder>
{
    public EmailFolderRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Employees"), "EmailFolders.json") // Search pattern irrelevant for custom load
    {
    }

    protected override string GetFilePath(EmailFolder item) =>
        Path.Combine(_basePath, item.EmployeeId, "Email", "Folders", $"{item.Id}.json");

    public override void LoadFromDisk()
    {
        if (!Directory.Exists(_basePath)) return;

        // Strategy: Iterate all Employee directories, look for Email/Folders
        var employeeDirs = Directory.GetDirectories(_basePath);
        foreach (var employeeDir in employeeDirs)
        {
            var folderPath = Path.Combine(employeeDir, "Email", "Folders");
            if (!Directory.Exists(folderPath)) continue;

            var files = Directory.GetFiles(folderPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var item = JsonSerializer.Deserialize<EmailFolder>(json);
                    if (item != null) _cache[item.Id] = item;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EmailFolderRepo] Error loading folder file {file}: {ex.Message}");
                }
            }
        }
    }

    public virtual List<EmailFolder> GetByEmployee(string employeeId) =>
        GetAll().Where(f => f.EmployeeId == employeeId).ToList();
}
