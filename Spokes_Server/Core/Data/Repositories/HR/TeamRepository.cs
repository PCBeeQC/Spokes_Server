using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class TeamRepository : JsonRepository<Team>
{
    public TeamRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Teams"))
    {
    }

    protected override string GetFilePath(Team entity)
    {
        return Path.Combine(_basePath, $"{entity.Id}.json");
    }
}


