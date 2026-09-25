using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class RateCardRepository : JsonRepository<RateCard>
{
    public RateCardRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "RateCards"), "*.json")
    {
    }

    protected override string GetFilePath(RateCard item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
