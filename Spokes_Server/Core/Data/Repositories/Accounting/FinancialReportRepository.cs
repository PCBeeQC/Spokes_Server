namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

public class FinancialReportRepository : JsonRepository<FinancialReport>
{
    private readonly SequenceService _sequences;

    public FinancialReportRepository(
        DiskPersistenceService writer,
        IConfiguration config,
        SequenceService sequences)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "FinancialReports"), "*.json")
    {
        _sequences = sequences;
    }

    protected override string GetFilePath(FinancialReport item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public string GenerateReportNumber() =>
        _sequences.GenerateNumber("FinancialReport", "FIN");

    public List<FinancialReport> GetOrdered()
    {
        return GetAll()
            .OrderByDescending(r => r.PeriodEnd)
            .ThenByDescending(r => r.UpdatedAt)
            .ToList();
    }
}
