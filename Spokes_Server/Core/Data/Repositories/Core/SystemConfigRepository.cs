using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class SystemConfigRepository : JsonRepository<SystemConfig>
{
    public SystemConfigRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings"), "system_config.json")
    {
    }

    protected override string GetFilePath(SystemConfig item) =>
        Path.Combine(_basePath, "system_config.json");

    /// <summary>
    /// Bootstraps the system configuration: creates and persists default configuration if missing from disk.
    /// </summary>
    public SystemConfig GetOrCreate()
    {
        EnsureLoaded();

        var config = _cache.Values.FirstOrDefault();
        if (config == null)
        {
            config = new SystemConfig();
            Save(config);
        }
        return config;
    }

    /// <summary>
    /// Pure query accessor to retrieve the active system configuration from memory without mutating disk state.
    /// Returns the cached configuration or a default in-memory instance if not yet initialized.
    /// </summary>
    public SystemConfig Get()
    {
        EnsureLoaded();
        return _cache.Values.FirstOrDefault() ?? new SystemConfig();
    }
}
