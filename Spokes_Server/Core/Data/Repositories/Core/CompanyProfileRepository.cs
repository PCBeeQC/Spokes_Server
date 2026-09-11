using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class CompanyProfileRepository : JsonRepository<CompanyProfile>
{
    public CompanyProfileRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings"), "company.json")
    {
    }

    protected override string GetFilePath(CompanyProfile item)
    {
        return Path.Combine(_basePath, "company.json");
    }

    // Helper to get the ONE profile (creates default if missing)
    public CompanyProfile Get()
    {
        var profile = _cache.Values.FirstOrDefault();
        if (profile == null)
        {
            profile = new CompanyProfile();
            Save(profile);
        }
        return profile;
    }
}


