namespace Spokes_Server.Core.Data.Repositories.HR;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.HR;

public class PayRunRepository : JsonRepository<PayRun>
{
    private readonly SequenceService _sequenceService;

    public PayRunRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "PayRuns"), "*.json")
    {
        _sequenceService = sequenceService;
    }

    protected override string GetFilePath(PayRun item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public string GenerateRunNumber(string prefix = "PAY") =>
        _sequenceService.GenerateNumber("PayRun", prefix);

    public List<PayRun> GetByTaxYear(int taxYear) =>
        GetAll()
            .Where(r => r.TaxYear == taxYear && !string.Equals(r.Status, PayRunStatus.Void, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.PayDate)
            .ThenBy(r => r.RunNumber)
            .ToList();
}
