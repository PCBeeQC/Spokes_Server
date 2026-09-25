using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class EmployerContributionRepository : JsonRepository<EmployerContribution>
{
    public EmployerContributionRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "EmployerContributions"), "contribution.json")
    {
    }

    protected override string GetFilePath(EmployerContribution item) =>
        Path.Combine(_basePath, item.Id, "contribution.json");
}


