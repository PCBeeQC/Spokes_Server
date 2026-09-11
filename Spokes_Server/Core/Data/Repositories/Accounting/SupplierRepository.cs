using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class SupplierRepository : JsonRepository<Supplier>
{
    public SupplierRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "Suppliers"), "*.json")
    {
    }

    protected override string GetFilePath(Supplier item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }
}


