using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class EmailFolderRepository : JsonRepository<EmailFolder>
{
    public EmailFolderRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public EmailFolderRepository(DiskPersistenceService writer, IConfiguration config, ILogger<EmailFolderRepository>? logger)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Employees"), "EmailFolders.json", logger) // Search pattern irrelevant for custom load
    {
    }

    protected override string GetFilePath(EmailFolder item) =>
        Path.Combine(_basePath, item.EmployeeId, "Email", "Folders", $"{item.Id}.json");

    public override void LoadFromDisk()
    {
        lock (_loadLock)
        {
            if (!Directory.Exists(_basePath))
            {
                _isLoaded = true;
                return;
            }

            var tempFolders = new List<EmailFolder>();
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
                        if (item != null) tempFolders.Add(item);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[EmailFolderRepo] Error loading folder file {File}", file);
                    }
                }
            }

            RawCache.Clear();
            foreach (var folder in tempFolders)
            {
                RawCache[folder.Id] = folder;
            }
            _isLoaded = true;
        }
    }

    public virtual List<EmailFolder> GetByEmployee(string employeeId) =>
        GetAll().Where(f => f.EmployeeId == employeeId).ToList();
}
