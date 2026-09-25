namespace Spokes_Server.Core.Data.Repositories.HR;

using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.HR;

public class HourBankAdjustmentRepository : JsonRepository<HourBankAdjustment>
{
    public HourBankAdjustmentRepository(DiskPersistenceService diskPersistence, IConfiguration config) 
        : base(diskPersistence, Path.Combine(config["DataPath"] ?? "Data", "HourBankAdjustments"))
    {
    }

    public List<HourBankAdjustment> GetByEmployeeId(string employeeId)
    {
        return GetAll().Where(a => a.EmployeeId == employeeId).ToList();
    }

    protected override string GetFilePath(HourBankAdjustment item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
