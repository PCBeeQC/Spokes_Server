using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.Accounting;

/// <summary>
/// A centralized engine for complex financial calculations to prevent logic drift.
/// Enforces decimal precision and consistent rounding.
/// </summary>
public class FinancialCalculationEngine
{
    private readonly InvoiceRepository _invoices;
    private readonly QuoteRepository _quotes;
    private readonly ProjectRepository _projects;

    public FinancialCalculationEngine(
        InvoiceRepository invoices,
        QuoteRepository quotes,
        ProjectRepository projects)
    {
        _invoices = invoices;
        _quotes = quotes;
        _projects = projects;
    }

    /// <summary>
    /// Accurately computes tax based on the subtotal and rate, rounding to 2 decimal places.
    /// </summary>
    public decimal CalculateTax(decimal subTotal, decimal taxRate)
    {
        return Math.Round(subTotal * taxRate, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Safely computes the margin percentage without divide-by-zero errors.
    /// </summary>
    public decimal CalculateMarginPercentage(decimal subTotal, decimal totalCost)
    {
        if (subTotal <= 0) return 0m;
        var margin = subTotal - totalCost;
        return Math.Round(margin / subTotal * 100m, 2, MidpointRounding.AwayFromZero);
    }

}
