namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

public class AccountingMigrationBatchRepository : JsonRepository<AccountingMigrationBatch>
{
    private readonly SequenceService _sequenceService;

    public AccountingMigrationBatchRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "AccountingMigrationBatches"), "*.json")
    {
        _sequenceService = sequenceService;
    }

    protected override string GetFilePath(AccountingMigrationBatch item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public string GenerateBatchNumber() =>
        _sequenceService.GenerateNumber("AccountingMigrationBatch", "MIG");

    public List<AccountingMigrationBatch> GetRecent() =>
        GetAll().OrderByDescending(b => b.ExecutedAtUtc).ToList();
}
