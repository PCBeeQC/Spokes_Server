using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class AlbumRepository : JsonRepository<Album>
{
    public AlbumRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Albums"), "*.json")
    {
    }

    protected override string GetFilePath(Album item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
