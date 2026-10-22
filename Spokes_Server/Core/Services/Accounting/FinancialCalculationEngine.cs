namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;

/// <summary>
/// Represents a cent-exact breakdown of subtotal, Quebec TPS (5%), TVQ (9.975%), and grand total
/// where <c>SubTotal + TpsAmount + TvqAmount == GrandTotal</c> is strictly guaranteed.
/// </summary>
public readonly record struct QuebecTaxBreakdown(
    decimal SubTotal,
    decimal TpsAmount,
    decimal TvqAmount,
    decimal TaxAmount,
    decimal GrandTotal,
    bool IsTaxExempt)
{
    public decimal TotalTax => TaxAmount;
}

/// <summary>
/// Represents a prorated detail line with reconciled quantity, unit price, line subtotal, and TPS/TVQ taxes.
/// </summary>
public readonly record struct ReconciledTaxLine(
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineSubTotal,
    decimal LineTps,
    decimal LineTvq,
    string? GlAccountOrItemCode = null);

/// <summary>
/// A centralized engine for complex financial calculations to prevent logic drift.
/// Enforces decimal precision and consistent rounding.
/// </summary>
public static class FinancialCalculationEngine
{
    public const decimal QuebecTpsRate = 0.05m;
    public const decimal QuebecTvqRate = 0.09975m;
    public const decimal QuebecCombinedTaxRate = 0.14975m;

    /// <summary>
    /// Accurately computes tax based on the subtotal and rate, rounding to 2 decimal places.
    /// </summary>
    public static decimal CalculateTax(decimal subTotal, decimal taxRate) =>
        Math.Round(subTotal * taxRate, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Safely computes the margin percentage without divide-by-zero errors.
    /// </summary>
    public static decimal CalculateMarginPercentage(decimal subTotal, decimal totalCost)
    {
        if (subTotal <= 0) return 0m;
        var margin = subTotal - totalCost;
        return Math.Round(margin / subTotal * 100m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Computes a cent-exact Quebec tax breakdown from a subtotal and effective tax rate,
    /// guaranteeing <c>SubTotal + TpsAmount + TvqAmount == GrandTotal</c>.
    /// </summary>
    public static QuebecTaxBreakdown CalculateFromSubTotal(decimal subTotal, decimal effectiveTaxRate = QuebecCombinedTaxRate)
    {
        var roundedSubTotal = Math.Round(subTotal, 2, MidpointRounding.AwayFromZero);
        if (effectiveTaxRate <= 0m || roundedSubTotal == 0m)
        {
            return new QuebecTaxBreakdown(roundedSubTotal, 0m, 0m, 0m, roundedSubTotal, effectiveTaxRate <= 0m);
        }

        var totalTax = Math.Round(roundedSubTotal * effectiveTaxRate, 2, MidpointRounding.AwayFromZero);
        decimal tps;
        decimal tvq;

        if (Math.Abs(effectiveTaxRate - QuebecCombinedTaxRate) < 0.0001m)
        {
            tps = Math.Round(roundedSubTotal * QuebecTpsRate, 2, MidpointRounding.AwayFromZero);
            tvq = totalTax - tps;
        }
        else if (Math.Abs(effectiveTaxRate - QuebecTpsRate) < 0.0001m)
        {
            tps = totalTax;
            tvq = 0m;
        }
        else
        {
            tps = Math.Round(totalTax * (QuebecTpsRate / QuebecCombinedTaxRate), 2, MidpointRounding.AwayFromZero);
            tvq = totalTax - tps;
        }

        var grandTotal = roundedSubTotal + totalTax;
        return new QuebecTaxBreakdown(roundedSubTotal, tps, tvq, totalTax, grandTotal, false);
    }

    /// <summary>
    /// Back-calculates a cent-exact Quebec tax breakdown from a tax-inclusive grand total,
    /// guaranteeing <c>SubTotal + TpsAmount + TvqAmount == GrandTotal</c>.
    /// </summary>
    public static QuebecTaxBreakdown CalculateFromGrandTotal(decimal grandTotal, bool isTaxExempt = false)
    {
        var roundedTotal = Math.Round(grandTotal, 2, MidpointRounding.AwayFromZero);
        if (isTaxExempt || roundedTotal == 0m)
        {
            return new QuebecTaxBreakdown(roundedTotal, 0m, 0m, 0m, roundedTotal, isTaxExempt);
        }

        var subTotal = Math.Round(roundedTotal / (1m + QuebecCombinedTaxRate), 2, MidpointRounding.AwayFromZero);
        var tps = Math.Round(subTotal * QuebecTpsRate, 2, MidpointRounding.AwayFromZero);
        var tvq = Math.Round(subTotal * QuebecTvqRate, 2, MidpointRounding.AwayFromZero);

        var diff = roundedTotal - (subTotal + tps + tvq);
        if (diff != 0m)
        {
            tvq += diff;
        }

        var taxAmount = tps + tvq;
        return new QuebecTaxBreakdown(subTotal, tps, tvq, taxAmount, roundedTotal, false);
    }

    /// <summary>
    /// Splits a combined Quebec tax amount into TPS (5%) and TVQ (9.975%) while guaranteeing <c>Tps + Tvq == totalTax</c>.
    /// </summary>
    public static (decimal Tps, decimal Tvq) SplitCombinedTax(decimal totalTax)
    {
        var roundedTax = Math.Round(totalTax, 2, MidpointRounding.AwayFromZero);
        if (roundedTax == 0m) return (0m, 0m);

        var tps = Math.Round(roundedTax * (QuebecTpsRate / QuebecCombinedTaxRate), 2, MidpointRounding.AwayFromZero);
        var tvq = roundedTax - tps;
        return (tps, tvq);
    }

    /// <summary>
    /// Normalizes quantity and unit price for a line item so that <c>Quantity * UnitPrice == LineSubTotal</c>
    /// at 2 decimal places (a strict requirement of Sage 50 .imp imports).
    /// </summary>
    public static (decimal NormalizedQuantity, decimal NormalizedUnitPrice) NormalizeQuantityAndPrice(decimal quantity, decimal lineSubTotal)
    {
        var roundedTotal = Math.Round(lineSubTotal, 2, MidpointRounding.AwayFromZero);
        var effectiveQty = quantity > 0m ? Math.Round(quantity, 4, MidpointRounding.AwayFromZero) : 1m;
        var candidatePrice = Math.Round(roundedTotal / effectiveQty, 2, MidpointRounding.AwayFromZero);

        if (Math.Round(effectiveQty * candidatePrice, 2, MidpointRounding.AwayFromZero) == roundedTotal)
        {
            return (effectiveQty, candidatePrice);
        }

        // Fallback when lineSubTotal / quantity is a repeating decimal (e.g. 100.00 / 3):
        // Use Quantity = 1 and UnitPrice = roundedTotal so Quantity * UnitPrice == LineSubTotal exactly.
        return (1m, roundedTotal);
    }

    /// <summary>
    /// Prorates a collection of raw line items across a target header subtotal, TPS, and TVQ so that:
    /// 1) Every line satisfies <c>Quantity * UnitPrice == LineSubTotal</c>
    /// 2) <c>Sum(LineSubTotal) == targetSubTotal</c>
    /// 3) <c>Sum(LineTps) == targetTps</c> and <c>Sum(LineTvq) == targetTvq</c>
    /// </summary>
    public static List<ReconciledTaxLine> ProrateLinesWithTaxReconciliation(
        IReadOnlyList<(string Description, decimal Quantity, decimal RawLineTotal, string? GlAccountOrItemCode)> rawLines,
        decimal targetSubTotal,
        decimal targetTps,
        decimal targetTvq,
        string? defaultGlOrItemCode = null)
    {
        var roundedSubTotal = Math.Round(targetSubTotal, 2, MidpointRounding.AwayFromZero);
        var roundedTps = Math.Round(targetTps, 2, MidpointRounding.AwayFromZero);
        var roundedTvq = Math.Round(targetTvq, 2, MidpointRounding.AwayFromZero);

        if (rawLines == null || rawLines.Count == 0)
        {
            return
            [
                new ReconciledTaxLine("Services", 1m, roundedSubTotal, roundedSubTotal, roundedTps, roundedTvq, defaultGlOrItemCode)
            ];
        }

        var result = new List<ReconciledTaxLine>(rawLines.Count);
        var rawSum = 0m;
        for (int i = 0; i < rawLines.Count; i++)
        {
            rawSum += Math.Round(rawLines[i].RawLineTotal, 2, MidpointRounding.AwayFromZero);
        }

        decimal runningSubTotal = 0m;
        decimal runningTps = 0m;
        decimal runningTvq = 0m;

        for (int i = 0; i < rawLines.Count; i++)
        {
            var raw = rawLines[i];
            bool isLast = i == rawLines.Count - 1;
            var code = !string.IsNullOrWhiteSpace(raw.GlAccountOrItemCode) ? raw.GlAccountOrItemCode : defaultGlOrItemCode;

            decimal lineSubTotal;
            decimal lineTps;
            decimal lineTvq;

            if (isLast)
            {
                lineSubTotal = roundedSubTotal - runningSubTotal;
                lineTps = roundedTps - runningTps;
                lineTvq = roundedTvq - runningTvq;
            }
            else
            {
                decimal ratio = rawSum != 0m
                    ? Math.Round(raw.RawLineTotal, 2, MidpointRounding.AwayFromZero) / rawSum
                    : 1m / rawLines.Count;

                lineSubTotal = Math.Round(roundedSubTotal * ratio, 2, MidpointRounding.AwayFromZero);
                lineTps = Math.Round(roundedTps * ratio, 2, MidpointRounding.AwayFromZero);
                lineTvq = Math.Round(roundedTvq * ratio, 2, MidpointRounding.AwayFromZero);

                runningSubTotal += lineSubTotal;
                runningTps += lineTps;
                runningTvq += lineTvq;
            }

            var (normQty, normPrice) = NormalizeQuantityAndPrice(raw.Quantity, lineSubTotal);
            result.Add(new ReconciledTaxLine(raw.Description, normQty, normPrice, lineSubTotal, lineTps, lineTvq, code));
        }

        return result;
    }
}
