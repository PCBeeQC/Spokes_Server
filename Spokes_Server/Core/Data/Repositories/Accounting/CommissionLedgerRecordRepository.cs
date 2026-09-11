using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class CommissionLedgerRecordRepository : JsonRepository<CommissionLedgerRecord>
{
    public CommissionLedgerRecordRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "CommissionLedgerRecords"), "*.json")
    {
    }

    protected override string GetFilePath(CommissionLedgerRecord item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    public List<CommissionLedgerRecord> GetByProject(string projectId)
    {
        return _cache.Values
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.DateGenerated)
            .ToList();
    }

    public List<CommissionLedgerRecord> GetByBeneficiary(string beneficiaryId)
    {
        return _cache.Values
            .Where(r => r.BeneficiaryId == beneficiaryId)
            .OrderByDescending(r => r.DateGenerated)
            .ToList();
    }
}
