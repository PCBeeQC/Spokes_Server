using Spokes_Server.Core.Models.Core;
using System.IO;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class ServerConfigRepository : JsonRepository<ServerConfig>
{
    private readonly Spokes_Server.Core.Services.Core.EncryptionService _encryptionService;

    public ServerConfigRepository(DiskPersistenceService writer, Microsoft.Extensions.Configuration.IConfiguration config, Spokes_Server.Core.Services.Core.EncryptionService encryptionService)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Global"), "ServerConfig.json")
    {
        _encryptionService = encryptionService;
    }

    protected override string GetFilePath(ServerConfig item)
    {
        return Path.Combine(_basePath, "ServerConfig.json");
    }

    public ServerConfig GetOrCreateGlobalConfig()
    {
        var config = GetById("Global");
        bool needsSave = false;

        if (config == null)
        {
            config = new ServerConfig { Id = "Global" };
            needsSave = true;
        }

        // Initialize Database Creation Version if missing (first boot for this DB)
        if (string.IsNullOrEmpty(config.DatabaseCreationVersion))
        {
            config.DatabaseCreationVersion = Spokes_Server.Core.Services.Licensing.LicenseValidationService.AppVersion;
            config.DatabaseCreationSignature = Spokes_Server.Core.Services.Licensing.LicenseValidationService.SignDemoVersion(config.DatabaseCreationVersion, _encryptionService.KeyHash);
            needsSave = true;
        }

        if (string.IsNullOrEmpty(config.DatabaseCreationId))
        {
            config.DatabaseCreationId = System.Guid.NewGuid().ToString();
            config.DatabaseCreationIdSignature = Spokes_Server.Core.Services.Licensing.LicenseValidationService.SignDemoVersion(config.DatabaseCreationId, _encryptionService.KeyHash);
            needsSave = true;
        }

        if (needsSave)
        {
            Save(config);
        }

        return config;
    }
}
