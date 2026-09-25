using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class ExpenseReportRepository : JsonRepository<ExpenseReport>
{
    private readonly SequenceService _sequenceService;

    public ExpenseReportRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Expenses"), "*.json")
    {
        _sequenceService = sequenceService;
    }

    protected override string GetFilePath(ExpenseReport item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    // Override Save to handle ID generation
    public override void Save(ExpenseReport item)
    {
        if (string.IsNullOrEmpty(item.ReportNumber))
        {
            item.ReportNumber = GenerateReportNumber();
        }
        base.Save(item);
    }

    private string GenerateReportNumber() =>
        _sequenceService.GenerateNumber("ExpenseReport", "EXP");
}


