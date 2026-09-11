using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class ExpenseReportRepository : JsonRepository<ExpenseReport>
{
    private readonly SequenceService _sequenceService;

    public ExpenseReportRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Expenses"), "*.json")
    {
        _sequenceService = sequenceService;
    }

    protected override string GetFilePath(ExpenseReport item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    // Override Save to handle ID generation
    public override void Save(ExpenseReport item)
    {
        if (string.IsNullOrEmpty(item.ReportNumber))
        {
            item.ReportNumber = GenerateReportNumber(item.DateCreated);
        }
        base.Save(item);
    }

    private string GenerateReportNumber(DateTime date)
    {
        return _sequenceService.GenerateNumber("ExpenseReport", "EXP");
    }
}


