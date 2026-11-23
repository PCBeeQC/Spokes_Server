namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Services.Accounting;

public class DirectTransactionRepository : JsonRepository<DirectTransaction>
{
    private readonly SequenceService? _sequences;

    public DirectTransactionRepository(
        DiskPersistenceService writer,
        IConfiguration config,
        SequenceService? sequences = null)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "DirectTransactions"), "*.json")
    {
        _sequences = sequences;
    }

    protected override string GetFilePath(DirectTransaction item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public string GenerateTransactionNumber()
    {
        return _sequences?.GenerateNumber("DirectTransaction", "TXN")
            ?? $"TXN-{DateTime.UtcNow:yyMM}-{Guid.NewGuid().ToString()[..4].ToUpperInvariant()}";
    }

    public List<DirectTransaction> GetOrdered()
    {
        return GetAll()
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.CreatedAt)
            .ToList();
    }

    public List<DirectTransaction> GetByDateRange(DateTime startDate, DateTime endDate)
    {
        var s = startDate.Date;
        var e = endDate.Date;
        return GetAll()
            .Where(t => t.Date.Date >= s && t.Date.Date <= e)
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.CreatedAt)
            .ToList();
    }

    public DirectTransaction? GetByBankLine(string statementId, string lineId)
    {
        if (string.IsNullOrWhiteSpace(statementId) || string.IsNullOrWhiteSpace(lineId))
            return null;

        return GetAll().FirstOrDefault(t =>
            string.Equals(t.LinkedBankStatementId, statementId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.LinkedBankLineId, lineId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Canonical single-source-of-truth projection of all non-void direct transactions (MoneyIn &amp; MoneyOut,
    /// excluding Transfers) attributed to <paramref name="projectId"/> in functional currency.
    /// Shared by ProjectReportService, ProjectOverview, AccountingService (commissions), and BusinessOverheadService.
    /// </summary>
    public ProjectDirectTransactionSummary SummarizeForProject(
        string projectId,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        var summary = new ProjectDirectTransactionSummary();
        if (string.IsNullOrWhiteSpace(projectId)) return summary;

        var startCutoff = startDate?.Date;
        var endCutoff = endDate?.Date;

        var candidates = GetAll()
            .Where(t => !t.IsVoid
                        && !string.Equals(t.Direction, DirectTransactionDirection.Transfer, StringComparison.OrdinalIgnoreCase)
                        && (!startCutoff.HasValue || t.Date.Date >= startCutoff.Value)
                        && (!endCutoff.HasValue || t.Date.Date <= endCutoff.Value))
            .OrderBy(t => t.Date)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        foreach (var tx in candidates)
        {
            var fx = tx.ExchangeRate > 0m ? tx.ExchangeRate : 1.0m;
            var isIncome = string.Equals(tx.Direction, DirectTransactionDirection.MoneyIn, StringComparison.OrdinalIgnoreCase);

            if (tx.Lines != null && tx.Lines.Count > 0)
            {
                foreach (var line in tx.Lines)
                {
                    if (line == null) continue;
                    var effectiveProjectId = !string.IsNullOrWhiteSpace(line.ProjectId) ? line.ProjectId : tx.ProjectId;
                    if (!string.Equals(effectiveProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var subFunc = FinancialCalculationEngine.RoundCurrency(line.SubTotal * fx);
                    var taxFunc = FinancialCalculationEngine.RoundCurrency(line.TaxAmount * fx);
                    var grandFunc = FinancialCalculationEngine.RoundCurrency(subFunc + taxFunc);
                    if (grandFunc == 0m && subFunc == 0m) continue;

                    var desc = !string.IsNullOrWhiteSpace(line.Description) ? line.Description : tx.Description;
                    summary.Items.Add(new ProjectDirectTransactionItem
                    {
                        TransactionId = tx.Id,
                        TransactionNumber = tx.TransactionNumber,
                        Date = tx.Date,
                        Direction = tx.Direction,
                        PartyName = tx.PartyName,
                        Description = desc,
                        GlAccountCode = line.GlAccountCode,
                        SubTotal = subFunc,
                        TaxAmount = taxFunc,
                        GrandTotal = grandFunc,
                        AttachmentPath = tx.AttachmentPath
                    });

                    if (isIncome)
                    {
                        summary.DirectIncomeSubTotal = FinancialCalculationEngine.RoundCurrency(summary.DirectIncomeSubTotal + subFunc);
                        summary.DirectIncomeTax = FinancialCalculationEngine.RoundCurrency(summary.DirectIncomeTax + taxFunc);
                    }
                    else
                    {
                        summary.DirectExpenseSubTotal = FinancialCalculationEngine.RoundCurrency(summary.DirectExpenseSubTotal + subFunc);
                        summary.DirectExpenseTax = FinancialCalculationEngine.RoundCurrency(summary.DirectExpenseTax + taxFunc);
                    }
                }
            }
            else if (string.Equals(tx.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                var subFunc = FinancialCalculationEngine.RoundCurrency(tx.SubTotal * fx);
                var taxFunc = FinancialCalculationEngine.RoundCurrency(tx.TaxAmount * fx);
                var grandFunc = FinancialCalculationEngine.RoundCurrency(subFunc + taxFunc);
                if (grandFunc == 0m && subFunc == 0m) continue;

                summary.Items.Add(new ProjectDirectTransactionItem
                {
                    TransactionId = tx.Id,
                    TransactionNumber = tx.TransactionNumber,
                    Date = tx.Date,
                    Direction = tx.Direction,
                    PartyName = tx.PartyName,
                    Description = tx.Description,
                    GlAccountCode = tx.PrimaryCategoryGlCode,
                    SubTotal = subFunc,
                    TaxAmount = taxFunc,
                    GrandTotal = grandFunc,
                    AttachmentPath = tx.AttachmentPath
                });

                if (isIncome)
                {
                    summary.DirectIncomeSubTotal = FinancialCalculationEngine.RoundCurrency(summary.DirectIncomeSubTotal + subFunc);
                    summary.DirectIncomeTax = FinancialCalculationEngine.RoundCurrency(summary.DirectIncomeTax + taxFunc);
                }
                else
                {
                    summary.DirectExpenseSubTotal = FinancialCalculationEngine.RoundCurrency(summary.DirectExpenseSubTotal + subFunc);
                    summary.DirectExpenseTax = FinancialCalculationEngine.RoundCurrency(summary.DirectExpenseTax + taxFunc);
                }
            }
        }

        return summary;
    }
}
