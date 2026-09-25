using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class SupplierRepository : JsonRepository<Supplier>
{
    public SupplierRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "Suppliers"), "*.json")
    {
    }

    protected override string GetFilePath(Supplier item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}


