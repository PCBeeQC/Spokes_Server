using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class PublicContactRepository : JsonRepository<ContactPerson>
{
    public PublicContactRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "PublicContacts"), "*.json")
    {
    }

    protected override string GetFilePath(ContactPerson item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }
}


