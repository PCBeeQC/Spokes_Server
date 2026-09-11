using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class SystemConfigRepository : JsonRepository<SystemConfig>
{
    public SystemConfigRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings"), "system_config.json")
    {
    }

    protected override string GetFilePath(SystemConfig item)
    {
        return Path.Combine(_basePath, "system_config.json");
    }

    // Helper to get the ONE system config (creates default if missing)
    public SystemConfig Get()
    {
        var config = _cache.Values.FirstOrDefault();
        if (config == null)
        {
            config = new SystemConfig();
            Save(config);
        }
        return config;
    }
}
