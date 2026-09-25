using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class RecurringExpenseRepository : JsonRepository<RecurringExpense>
{
    public RecurringExpenseRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "RecurringExpenses"), "expense.json")
    {
    }

    protected override string GetFilePath(RecurringExpense item) =>
        Path.Combine(_basePath, item.Id, "expense.json");
}


