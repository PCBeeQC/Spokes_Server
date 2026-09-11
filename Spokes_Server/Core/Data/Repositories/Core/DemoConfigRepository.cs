using Spokes_Server.Core.Models.Core;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class DemoConfigRepository : JsonRepository<DemoConfig>
{
    public DemoConfigRepository(DiskPersistenceService writer, IConfiguration config) 
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Backups", "DemoConfig"), "*.json")
    {
    }

    protected override string GetFilePath(DemoConfig item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    public DemoConfig Get()
    {
        var config = GetById("demo-config");
        if (config == null)
        {
            config = new DemoConfig();
            Save(config);
        }
        return config;
    }
}
