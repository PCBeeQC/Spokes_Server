using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class EmployerContributionRepository : JsonRepository<EmployerContribution>
{
    public EmployerContributionRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "EmployerContributions"), "contribution.json")
    {
    }

    protected override string GetFilePath(EmployerContribution item)
    {
        // Simple list, but we use ID folders generally in this project to avoid huge single JSON files if items grow
        // However, standard pattern here seems to be folders per item.
        return Path.Combine(_basePath, item.Id, "contribution.json");
    }
}


