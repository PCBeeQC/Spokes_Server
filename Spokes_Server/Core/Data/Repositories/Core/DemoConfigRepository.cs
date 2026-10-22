using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class DemoConfigRepository : JsonRepository<DemoConfig>
{
    public DemoConfigRepository(DiskPersistenceService writer, IConfiguration config) 
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Backups", "DemoConfig"), "*.json")
    {
    }

    protected override string GetFilePath(DemoConfig item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    /// <summary>
    /// Bootstraps the demo configuration: creates and persists default demo config if missing from disk.
    /// </summary>
    public DemoConfig GetOrCreate()
    {
        var config = GetById("demo-config");
        if (config == null)
        {
            config = new DemoConfig();
            Save(config);
        }
        return config;
    }

    /// <summary>
    /// Pure query accessor to retrieve the active demo configuration without mutating disk state.
    /// Returns the cached config or a default in-memory instance if not yet initialized.
    /// </summary>
    public DemoConfig Get()
    {
        return GetById("demo-config") ?? new DemoConfig();
    }
}
