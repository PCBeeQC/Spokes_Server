using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class ServerConfigRepository : JsonRepository<ServerConfig>
{
    private readonly EncryptionService _encryptionService;

    public ServerConfigRepository(DiskPersistenceService writer, IConfiguration config, EncryptionService encryptionService)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Global"), "ServerConfig.json")
    {
        _encryptionService = encryptionService;
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

        // Initialize Database Creation Version if missing (first boot for this DB)
        if (string.IsNullOrEmpty(config.DatabaseCreationVersion))
        {
            config.DatabaseCreationVersion = LicenseValidationService.AppVersion;
            config.DatabaseCreationSignature = LicenseValidationService.SignDemoVersion(config.DatabaseCreationVersion, _encryptionService.KeyHash);
            needsSave = true;
        }

        if (string.IsNullOrEmpty(config.DatabaseCreationId))
        {
            config.DatabaseCreationId = Guid.NewGuid().ToString();
            config.DatabaseCreationIdSignature = LicenseValidationService.SignDemoVersion(config.DatabaseCreationId, _encryptionService.KeyHash);
            needsSave = true;
        }

        if (needsSave)
        {
            Save(config);
        }

        return config;
    }
}
