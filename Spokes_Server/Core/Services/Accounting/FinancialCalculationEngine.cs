namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Spokes_Server.Core.Models.Accounting;

public readonly record struct TaxBreakdown(
    decimal SubTotal,
    decimal TpsAmount,
    decimal TvqAmount,
    decimal TaxAmount,
    decimal GrandTotal)
{
    public decimal TotalTax => TaxAmount;
}

/// <summary>
/// Centralized, deterministic financial calculation engine for Spokes.
/// Supports both Canada/Quebec GST/TPS + QST/TVQ calculations and declarative multi-jurisdiction
/// tax and mileage rules from <see cref="JurisdictionTemplate"/>.
/// Uses <see cref="MidpointRounding.AwayFromZero"/> consistently across all calculations.
/// </summary>
public static class FinancialCalculationEngine
{
    public const decimal QuebecTpsRate = 0.05m;
    public const decimal QuebecTvqRate = 0.09975m;
    public const decimal QuebecCombinedTaxRate = QuebecTpsRate + QuebecTvqRate; // 0.14975m

    private static volatile JurisdictionTemplate? _activeTemplate;

    /// <summary>
    /// Gets or sets the active jurisdiction template for the server instance.
    /// Defaults to Canada — Québec (CA-QC) when null.
    /// </summary>
    public static JurisdictionTemplate? ActiveTemplate
    {
        get => _activeTemplate;
        set => _activeTemplate = value;
    }

    /// <summary>
    /// Returns the combined sales tax rate from the active jurisdiction template, or <see cref="QuebecCombinedTaxRate"/> as fallback.
    /// </summary>
    public static decimal ActiveCombinedTaxRate =>
        _activeTemplate?.SalesTaxes is { Count: > 0 } taxes
            ? taxes.Sum(t => t.Rate)
            : QuebecCombinedTaxRate;

    /// <summary>
    /// Calculates SubTotal, TPS, TVQ, TaxAmount, and GrandTotal from a tax-included GrandTotal.
    /// Guarantees that <c>SubTotal + TpsAmount + TvqAmount == GrandTotal</c> to the exact cent.
    /// </summary>
    public static TaxBreakdown CalculateFromGrandTotal(decimal grandTotal, bool isTaxExempt = false)
    {
        var roundedTotal = RoundCurrency(grandTotal);
        if (roundedTotal == 0m)
        {
            return new TaxBreakdown(0m, 0m, 0m, 0m, 0m);
        }

        if (isTaxExempt)
        {
            return new TaxBreakdown(roundedTotal, 0m, 0m, 0m, roundedTotal);
        }

        var activeTaxes = _activeTemplate?.SalesTaxes;
        if (activeTaxes is { Count: > 0 } && !IsStandardQuebecTaxSet(activeTaxes))
        {
            var combinedRate = activeTaxes.Sum(t => t.Rate);
            if (combinedRate <= 0m)
            {
                return new TaxBreakdown(roundedTotal, 0m, 0m, 0m, roundedTotal);
            }

            var approxSub = roundedTotal / (1m + combinedRate);
            var primaryRate = activeTaxes[0].Rate;
            var secondaryRate = activeTaxes.Count > 1 ? activeTaxes.Skip(1).Sum(t => t.Rate) : 0m;
            var t1 = RoundCurrency(approxSub * primaryRate);
            var t2 = RoundCurrency(approxSub * secondaryRate);
            var totTax = t1 + t2;
            var netSub = roundedTotal - totTax;
            return new TaxBreakdown(netSub, t1, t2, totTax, roundedTotal);
        }

        var rawSubTotal = roundedTotal / (1m + QuebecCombinedTaxRate);
        var tps = RoundCurrency(rawSubTotal * QuebecTpsRate);
        var tvq = RoundCurrency(rawSubTotal * QuebecTvqRate);
        var taxAmount = tps + tvq;
        var subTotal = roundedTotal - taxAmount;

        return new TaxBreakdown(subTotal, tps, tvq, taxAmount, roundedTotal);
    }

    /// <summary>
    /// Calculates TPS, TVQ, TaxAmount, and GrandTotal from a pre-tax SubTotal.
    /// </summary>
    public static TaxBreakdown CalculateFromSubTotal(decimal subTotal, bool isTaxExempt = false)
    {
        var roundedSubTotal = RoundCurrency(subTotal);
        if (roundedSubTotal == 0m)
        {
            return new TaxBreakdown(0m, 0m, 0m, 0m, 0m);
        }

        if (isTaxExempt)
        {
            return new TaxBreakdown(roundedSubTotal, 0m, 0m, 0m, roundedSubTotal);
        }

        var activeTaxes = _activeTemplate?.SalesTaxes;
        if (activeTaxes is { Count: > 0 } && !IsStandardQuebecTaxSet(activeTaxes))
        {
            var components = BuildTaxComponentsFromSubTotal(roundedSubTotal, activeTaxes, isTaxExempt: false);
            var t1 = components.Count > 0 ? components[0].Amount : 0m;
            var t2 = components.Count > 1 ? components.Skip(1).Sum(c => c.Amount) : 0m;
            var totTax = t1 + t2;
            return new TaxBreakdown(roundedSubTotal, t1, t2, totTax, roundedSubTotal + totTax);
        }

        var tps = CalculateTps(roundedSubTotal);
        var tvq = CalculateTvq(roundedSubTotal);
        var taxAmount = tps + tvq;
        var grandTotal = roundedSubTotal + taxAmount;

        return new TaxBreakdown(roundedSubTotal, tps, tvq, taxAmount, grandTotal);
    }

    /// <summary>
    /// Calculates tax breakdown from a SubTotal and a custom combined tax rate (e.g. from an Invoice or PurchaseOrder).
    /// When the custom tax rate matches the standard Quebec combined rate, uses exact TPS/TVQ rates;
    /// otherwise scales TPS/TVQ proportionally so their sum equals the custom tax amount.
    /// </summary>
    public static TaxBreakdown CalculateFromSubTotal(decimal subTotal, decimal customTaxRate)
    {
        var roundedSubTotal = RoundCurrency(subTotal);
        if (roundedSubTotal == 0m || customTaxRate <= 0m)
        {
            return new TaxBreakdown(roundedSubTotal, 0m, 0m, 0m, roundedSubTotal);
        }

        if (Math.Abs(customTaxRate - QuebecCombinedTaxRate) < 0.00001m)
        {
            return CalculateFromSubTotal(roundedSubTotal, isTaxExempt: false);
        }

        var activeTaxes = _activeTemplate?.SalesTaxes;
        if (activeTaxes is { Count: 1 } && Math.Abs(customTaxRate - activeTaxes[0].Rate) < 0.00001m)
        {
            var singleTax = RoundCurrency(roundedSubTotal * customTaxRate);
            return new TaxBreakdown(roundedSubTotal, singleTax, 0m, singleTax, roundedSubTotal + singleTax);
        }

        var totalTax = RoundCurrency(roundedSubTotal * customTaxRate);
        var tpsShare = RoundCurrency(totalTax * (QuebecTpsRate / QuebecCombinedTaxRate));
        var tvqShare = totalTax - tpsShare;

        return new TaxBreakdown(roundedSubTotal, tpsShare, tvqShare, totalTax, roundedSubTotal + totalTax);
    }

    /// <summary>
    /// Builds explicit <see cref="DocumentTaxLine"/> components for a pre-tax subtotal using the provided or active <see cref="SalesTaxRule"/> list.
    /// </summary>
    public static List<DocumentTaxLine> BuildTaxComponentsFromSubTotal(
        decimal subTotal,
        IReadOnlyList<SalesTaxRule>? taxRules = null,
        bool isTaxExempt = false,
        bool forPurchases = false)
    {
        var roundedSub = RoundCurrency(subTotal);
        if (isTaxExempt || roundedSub == 0m) return [];

        var rules = taxRules ?? _activeTemplate?.SalesTaxes;
        if (rules == null || rules.Count == 0)
        {
            var qc = CalculateFromSubTotal(roundedSub, isTaxExempt: false);
            return
            [
                new DocumentTaxLine
                {
                    Code = "TPS",
                    Name = "GST / TPS (5%)",
                    Rate = QuebecTpsRate,
                    Amount = qc.TpsAmount,
                    IsRecoverable = true,
                    AuthorityName = "Revenu Québec / CRA",
                    GlAccountCode = forPurchases ? "2165" : "2160"
                },
                new DocumentTaxLine
                {
                    Code = "TVQ",
                    Name = "QST / TVQ (9.975%)",
                    Rate = QuebecTvqRate,
                    Amount = qc.TvqAmount,
                    IsRecoverable = true,
                    AuthorityName = "Revenu Québec",
                    GlAccountCode = forPurchases ? "2175" : "2170"
                }
            ];
        }

        var lines = new List<DocumentTaxLine>(rules.Count);
        decimal runningTaxTotal = 0m;
        foreach (var rule in rules)
        {
            if (rule.Rate <= 0m) continue;
            var taxableBase = rule.IsCompound ? (roundedSub + runningTaxTotal) : roundedSub;
            var taxAmt = RoundCurrency(taxableBase * rule.Rate);
            runningTaxTotal += taxAmt;
            lines.Add(new DocumentTaxLine
            {
                Code = rule.Code,
                Name = rule.Name,
                Rate = rule.Rate,
                Amount = taxAmt,
                IsRecoverable = rule.IsRecoverable,
                AuthorityName = rule.AuthorityName,
                GlAccountCode = forPurchases
                    ? (!string.IsNullOrWhiteSpace(rule.RecoverableAssetGlCode) ? rule.RecoverableAssetGlCode : rule.CollectedLiabilityGlCode)
                    : rule.CollectedLiabilityGlCode
            });
        }
        return lines;
    }

    /// <summary>
    /// Extracts pre-tax SubTotal and explicit multi-jurisdiction <see cref="DocumentTaxLine"/> components from a
    /// tax-included GrandTotal, guaranteeing <c>SubTotal + Components.Sum(c =&gt; c.Amount) == RoundCurrency(grandTotal)</c>.
    /// </summary>
    public static (decimal SubTotal, List<DocumentTaxLine> Components) BuildTaxComponentsFromGrandTotal(
        decimal grandTotal,
        IReadOnlyList<SalesTaxRule>? taxRules = null,
        bool isTaxExempt = false,
        bool forPurchases = false)
    {
        var roundedGrand = RoundCurrency(grandTotal);
        if (roundedGrand == 0m || isTaxExempt)
        {
            return (roundedGrand, []);
        }

        var rules = taxRules ?? _activeTemplate?.SalesTaxes;
        if (rules == null || rules.Count == 0)
        {
            var qc = CalculateFromGrandTotal(roundedGrand, isTaxExempt: false);
            var fallbackLines = new List<DocumentTaxLine>
            {
                new()
                {
                    Code = "TPS",
                    Name = "GST / TPS (5%)",
                    Rate = QuebecTpsRate,
                    Amount = qc.TpsAmount,
                    IsRecoverable = true,
                    AuthorityName = "Revenu Québec / CRA",
                    GlAccountCode = forPurchases ? "2165" : "2160"
                },
                new()
                {
                    Code = "TVQ",
                    Name = "QST / TVQ (9.975%)",
                    Rate = QuebecTvqRate,
                    Amount = qc.TvqAmount,
                    IsRecoverable = true,
                    AuthorityName = "Revenu Québec",
                    GlAccountCode = forPurchases ? "2175" : "2170"
                }
            };
            return (qc.SubTotal, fallbackLines);
        }

        // Compute effective combined multiplier (handling compound taxes accurately)
        decimal effectiveMultiplier = 1m;
        decimal simpleRateSum = 0m;
        foreach (var rule in rules)
        {
            if (rule.Rate <= 0m) continue;
            if (rule.IsCompound)
            {
                effectiveMultiplier *= (1m + rule.Rate);
            }
            else
            {
                simpleRateSum += rule.Rate;
            }
        }
        effectiveMultiplier = (1m + simpleRateSum) * effectiveMultiplier;

        if (effectiveMultiplier <= 1m)
        {
            return (roundedGrand, []);
        }

        var rawSubTotal = roundedGrand / effectiveMultiplier;
        var components = new List<DocumentTaxLine>(rules.Count);
        decimal runningTaxTotal = 0m;
        foreach (var rule in rules)
        {
            if (rule.Rate <= 0m) continue;
            var taxableBase = rule.IsCompound ? (rawSubTotal + runningTaxTotal) : rawSubTotal;
            var taxAmt = RoundCurrency(taxableBase * rule.Rate);
            runningTaxTotal += taxAmt;
            components.Add(new DocumentTaxLine
            {
                Code = rule.Code,
                Name = rule.Name,
                Rate = rule.Rate,
                Amount = taxAmt,
                IsRecoverable = rule.IsRecoverable,
                AuthorityName = rule.AuthorityName,
                GlAccountCode = forPurchases
                    ? (!string.IsNullOrWhiteSpace(rule.RecoverableAssetGlCode) ? rule.RecoverableAssetGlCode : rule.CollectedLiabilityGlCode)
                    : rule.CollectedLiabilityGlCode
            });
        }

        var netSubTotal = RoundCurrency(roundedGrand - components.Sum(c => c.Amount));
        return (netSubTotal, components);
    }

    /// <summary>
    /// Calculates the statutory tax-free mileage allowance for a trip of <paramref name="kilometers"/>,
    /// optionally accounting for <paramref name="priorYtdKilometers"/> when a 2-tier threshold (e.g. 5,000 km) is configured.
    /// </summary>
    public static (decimal TotalAllowance, decimal EffectiveRatePerKm) CalculateMileageAllowance(
        decimal kilometers,
        decimal priorYtdKilometers = 0m,
        MileageAllowanceRule? rule = null)
    {
        if (kilometers <= 0m)
        {
            var defaultRate = rule?.PrimaryRatePerKm ?? _activeTemplate?.MileageRules.PrimaryRatePerKm ?? 0.70m;
            return (0m, defaultRate);
        }

        var activeRule = rule ?? _activeTemplate?.MileageRules ?? new MileageAllowanceRule();
        if (!activeRule.TierThresholdKm.HasValue || activeRule.TierThresholdKm.Value <= 0m)
        {
            var amount = RoundCurrency(kilometers * activeRule.PrimaryRatePerKm);
            return (amount, activeRule.PrimaryRatePerKm);
        }

        var threshold = activeRule.TierThresholdKm.Value;
        var prior = Math.Max(0m, priorYtdKilometers);

        decimal primaryKm;
        decimal secondaryKm;
        if (prior >= threshold)
        {
            primaryKm = 0m;
            secondaryKm = kilometers;
        }
        else if (prior + kilometers <= threshold)
        {
            primaryKm = kilometers;
            secondaryKm = 0m;
        }
        else
        {
            primaryKm = threshold - prior;
            secondaryKm = kilometers - primaryKm;
        }

        var totalAllowance = RoundCurrency((primaryKm * activeRule.PrimaryRatePerKm) + (secondaryKm * activeRule.SecondaryRatePerKm));
        var effectiveRate = Math.Round(totalAllowance / kilometers, 4, MidpointRounding.AwayFromZero);
        return (totalAllowance, effectiveRate);
    }

    public static decimal CalculateTps(decimal subTotal, bool isTaxExempt = false) =>
        isTaxExempt ? 0m : RoundCurrency(subTotal * QuebecTpsRate);

    public static decimal CalculateTvq(decimal subTotal, bool isTaxExempt = false) =>
        isTaxExempt ? 0m : RoundCurrency(subTotal * QuebecTvqRate);

    public static decimal CalculateTax(decimal subtotal, decimal taxRate)
    {
        if (subtotal == 0m || taxRate == 0m) return 0.00m;
        return RoundCurrency(subtotal * taxRate);
    }

    public static decimal CalculateMarginPercentage(decimal subtotal, decimal totalCost)
    {
        if (subtotal <= 0m) return 0m;
        return Math.Round(((subtotal - totalCost) / subtotal) * 100m, 2, MidpointRounding.AwayFromZero);
    }

    public static decimal RoundCurrency(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Returns true if a recurring bill history record is already covered by a linked standalone Bill
    /// (same year/month or matching invoice number), preventing double-counting in ledger/tax calculations.
    /// </summary>
    public static bool IsRecurringRecordCoveredByBills(RecurringBillRecord record, IEnumerable<Bill> linkedBills) =>
        linkedBills.Any(b =>
            (b.Date.Year == record.Date.Year && b.Date.Month == record.Date.Month) ||
            (!string.IsNullOrWhiteSpace(record.InvoiceNumber) &&
             string.Equals(b.InvoiceNumber, record.InvoiceNumber, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Resolves effective pre-tax subtotal and TPS/TVQ tax split for a recurring bill history record.
    /// </summary>
    public static (decimal SubTotal, decimal Tps, decimal Tvq, bool IsExempt) ResolveRecurringRecordTaxSplit(
        RecurringExpense profile,
        RecurringBillRecord record)
    {
        var isExempt = record.IsTaxExempt ?? profile.IsTaxExempt;
        if (record.SubTotal > 0m || record.TpsAmount > 0m || record.TvqAmount > 0m)
        {
            var tps = isExempt ? 0m : record.TpsAmount;
            var tvq = isExempt ? 0m : record.TvqAmount;
            var subTotal = record.SubTotal > 0m ? record.SubTotal : Math.Max(0m, record.Amount - (tps + tvq));
            return (subTotal, tps, tvq, isExempt);
        }

        var calc = CalculateFromGrandTotal(record.Amount, isExempt);
        return (calc.SubTotal, calc.TpsAmount, calc.TvqAmount, isExempt);
    }

    /// <summary>
    /// Standard numeric format string for editing/displaying tax rate percentages without trailing zeros
    /// (supports up to 4 fractional percent digits, matching 6-decimal-place fractional rate storage).
    /// </summary>
    public const string TaxRatePercentFormat = "0.####";

    /// <summary>
    /// Formats a fractional tax rate (e.g. <c>0.14975m</c>, <c>0.05m</c>) as a percentage string
    /// trimmed to the last significant digit with no trailing zeros (e.g. <c>"14.975"</c>, <c>"5"</c>).
    /// </summary>
    public static string FormatTaxRatePercent(decimal taxRate, IFormatProvider? formatProvider = null)
    {
        var percent = taxRate * 100m;
        if (percent == 0m)
        {
            return "0";
        }

        return percent.ToString("0.############################", formatProvider ?? CultureInfo.CurrentCulture);
    }

    private static bool IsStandardQuebecTaxSet(IReadOnlyList<SalesTaxRule> taxes)
    {
        if (taxes.Count != 2) return false;
        return taxes.Any(t => Math.Abs(t.Rate - QuebecTpsRate) < 0.00001m) &&
               taxes.Any(t => Math.Abs(t.Rate - QuebecTvqRate) < 0.00001m);
    }
}
