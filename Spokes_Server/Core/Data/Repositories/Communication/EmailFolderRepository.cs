using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class EmailFolderRepository : JsonRepository<EmailFolder>
{
    public EmailFolderRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Employees"), "EmailFolders.json") // Search pattern irrelevant for custom load
    {
    }

    protected override string GetFilePath(EmailFolder item)
    {
        // Data/Employees/{EmployeeId}/Email/Folders/{Id}.json
        return Path.Combine(_basePath, item.EmployeeId, "Email", "Folders", $"{item.Id}.json");
    }

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
                    var item = System.Text.Json.JsonSerializer.Deserialize<EmailFolder>(json);
                    if (item != null) _cache[item.Id] = item;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EmailFolderRepo] Error loading folder file {file}: {ex.Message}");
                }
            }
        }
    }

    public virtual List<EmailFolder> GetByEmployee(string employeeId)
    {
        return GetAll().Where(f => f.EmployeeId == employeeId).ToList();
    }
}


