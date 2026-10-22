using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class ServerConfigRepository : JsonRepository<ServerConfig>
{
    public ServerConfigRepository(
        DiskPersistenceService writer,
        IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Global"), "ServerConfig.json")
    {
    }

    protected override string GetFilePath(ServerConfig item) =>
        Path.Combine(_basePath, "ServerConfig.json");

    public ServerConfig GetOrCreateGlobalConfig()
    {
        var config = GetById("Global");
        bool needsSave = false;

        if (config == null)
        {
            config = new ServerConfig { Id = "Global" };
            needsSave = true;
        }

        if (string.IsNullOrEmpty(config.DatabaseCreationId))
        {
            config.DatabaseCreationId = Guid.NewGuid().ToString();
            needsSave = true;
        }

        if (needsSave)
        {
            Save(config);
        }

        return config;
    }
}

