using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class PurchaseOrderRepository : JsonRepository<PurchaseOrder>
{
    private readonly SequenceService _sequenceService;

    [ActivatorUtilitiesConstructor]
    public PurchaseOrderRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Purchases"), "*.json")
    {
        _sequenceService = sequenceService;
    }

    public PurchaseOrderRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, new SequenceService(config))
    {
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

    public string GeneratePoNumber(string prefix) =>
        _sequenceService.GenerateNumber("PurchaseOrder", prefix);
}
