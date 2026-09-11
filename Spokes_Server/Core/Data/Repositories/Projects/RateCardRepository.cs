using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class RateCardRepository : JsonRepository<RateCard>
{
    public RateCardRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "RateCards"), "*.json")
    {
    }

    protected override string GetFilePath(RateCard item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }
}


