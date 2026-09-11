using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class PurchaseOrderRepository : JsonRepository<PurchaseOrder>
{
    public PurchaseOrderRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Purchases"), "*.json")
    {
    }

    protected override string GetFilePath(PurchaseOrder item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    // Override Save to handle ID generation
    public override void Save(PurchaseOrder item)
    {
        if (string.IsNullOrEmpty(item.PoNumber))
        {
            item.PoNumber = GeneratePoNumber(item.Date);
        }
        base.Save(item);
    }

    private string GeneratePoNumber(DateTime date)
    {
        // Format: PO2512-
        string prefix = $"PO{date:yyMM}-";

        // Find existing POs with this prefix to determine the increment
        var existingCount = _cache.Values.Count(p => p.PoNumber.StartsWith(prefix));

        // Format: PO2512-01
        return $"{prefix}{(existingCount + 1):00}";
    }
}


