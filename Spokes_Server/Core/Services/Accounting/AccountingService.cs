using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.Accounting;

public class AccountingService
{
    private readonly Database _db;
    private readonly FinancialCalculationEngine _calcEngine;

    public AccountingService(Database db, FinancialCalculationEngine calcEngine)
    {
        _db = db;
        _calcEngine = calcEngine;
    }

    public void ProcessInvoiceStatusChange(Invoice invoice, string oldStatus, string newStatus, string user)
    {
        Projects.TimelineEventHelper.ProcessInvoiceStatusChange(_db, invoice, oldStatus, newStatus, user);
    }

    public void EvaluateProjectCommissions(string projectId)
    {
        Projects.TimelineEventHelper.EvaluateProjectCommissions(_db, projectId);
    }

    public decimal GetProjectGrossMargin(string projectId)
    {
        var quotes = _db.Quotes.GetByProject(projectId);
        var quoteRevenue = quotes.Where(q => q.Status == "Accepted").Sum(q => q.GrandTotal);
        var invoices = _db.Invoices.GetByProject(projectId).Where(i => i.Status != "Void").ToList();
        var totalIncome = invoices.Any() ? invoices.Sum(i => i.SubTotal) : quoteRevenue;

        var allPos = _db.Purchases.GetAll().Where(p => p.Status != "Void" && p.Items.Any(i => i.ProjectId == projectId)).ToList();
        var totalPurchases = allPos.SelectMany(p => p.Items.Where(i => i.ProjectId == projectId)).Sum(i => i.Total);

        var allExpenses = _db.ExpenseReports.GetAll().SelectMany(r => r.Items).Where(i => i.ProjectId == projectId).ToList();
        var totalExpenses = allExpenses.Sum(e => e.Total);

        return totalIncome - (totalPurchases + totalExpenses);
    }
}
