using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class TeamRepository : JsonRepository<Team>
{
    public TeamRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Teams"))
    {
    }

    protected override string GetFilePath(Team entity) =>
        Path.Combine(_basePath, $"{entity.Id}.json");
}
