using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class PurchaseOrderRepository : JsonRepository<PurchaseOrder>
{
    private readonly SequenceService _sequenceService;

    public PurchaseOrderRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Purchases"), "*.json")
    {
        _sequenceService = sequenceService;
    }

    protected override string GetFilePath(PurchaseOrder item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    // Override Save to handle ID generation
    public override void Save(PurchaseOrder item)
    {
        if (string.IsNullOrEmpty(item.PoNumber))
        {
            item.PoNumber = GeneratePoNumber(item.Date);
        }
        base.Save(item);
    }

    public string GeneratePoNumber(DateTime date)
    {
        var seq = _sequenceService.GetNextSequence($"PurchaseOrder_{date:yyMM}");
        return $"PO{date:yyMM}-{seq:00}";
    }

    public List<ProjectPurchaseRow> GetProjectPurchaseRows(string projectId)
    {
        return GetAll()
            .Where(p => p.Status != PurchaseOrderStatus.Void)
            .SelectMany(po => po.LineItems
                .Where(item => item.ProjectId == projectId)
                .Select(item => new ProjectPurchaseRow
                {
                    Id = po.Id,
                    Date = po.Date,
                    PoNumber = po.PoNumber,
                    Description = item.Description,
                    PartNumber = item.PartNumber,
                    NetTotal = item.Total,
                    TaxAmount = po.ApplyTax ? item.Total * po.TaxRate : 0m
                }))
            .OrderByDescending(row => row.Date)
            .ToList();
    }
}
