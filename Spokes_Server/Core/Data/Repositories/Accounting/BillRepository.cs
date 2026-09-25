using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class BillRepository : JsonRepository<Bill>
{
    public BillRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "Bills"), "*.json")
    {
    }

    protected override string GetFilePath(Bill item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public List<Bill> GetByPo(string poId) =>
        GetAll().Where(b => b.PurchaseOrderId == poId).ToList();

    public List<Bill> GetUnpaid() =>
        GetAll().Where(b => b.Status != "Paid" && b.Status != "Void").ToList();
}


