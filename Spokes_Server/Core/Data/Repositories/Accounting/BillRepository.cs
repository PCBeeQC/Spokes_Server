using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class BillRepository : JsonRepository<Bill>
{
    public BillRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "Bills"), "*.json")
    {
    }

    protected override string GetFilePath(Bill item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    public List<Bill> GetByPo(string poId)
    {
        return GetAll().Where(b => b.PurchaseOrderId == poId).ToList();
    }

    public List<Bill> GetUnpaid()
    {
        return GetAll().Where(b => b.Status != "Paid" && b.Status != "Void").ToList();
    }
}


