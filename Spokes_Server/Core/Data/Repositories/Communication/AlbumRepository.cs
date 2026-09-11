using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Data;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class AlbumRepository : JsonRepository<Album>
{
    public AlbumRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Albums"), "*.json")
    {
    }

    protected override string GetFilePath(Album item)
    {
        return System.IO.Path.Combine(_basePath, $"{item.Id}.json");
    }
}
