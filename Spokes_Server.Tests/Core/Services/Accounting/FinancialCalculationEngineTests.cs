using System.Globalization;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;

namespace Spokes_Server.Tests.Core.Services.Accounting;

[Collection("JurisdictionStaticState")]
public class FinancialCalculationEngineTests : IDisposable
{
    public FinancialCalculationEngineTests()
    {
        FinancialCalculationEngine.ActiveTemplate = null;
    }

    public void Dispose()
    {
        FinancialCalculationEngine.ActiveTemplate = null;
    }
    [Theory]
    [InlineData(100.00, 0.15, 15.00)]
    [InlineData(250.50, 0.05, 12.53)]
    [InlineData(50.00, 0.09975, 4.99)]
    public void CalculateTax_PositiveSubtotalAndTaxRate_CalculatesExpectedTax(decimal subtotal, decimal taxRate, decimal expected)
    {
        var result = FinancialCalculationEngine.CalculateTax(subtotal, taxRate);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(10.055, 1.0, 10.06)]
    [InlineData(10.025, 1.0, 10.03)]
    [InlineData(100.00, 0.05555, 5.56)]
    [InlineData(-10.055, 1.0, -10.06)]
    [InlineData(-10.025, 1.0, -10.03)]
    public void CalculateTax_MidpointRounding_RoundsAwayFromZero(decimal subtotal, decimal taxRate, decimal expected)
    {
        var result = FinancialCalculationEngine.CalculateTax(subtotal, taxRate);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculateTax_ZeroSubtotal_ReturnsZero()
    {
        var result = FinancialCalculationEngine.CalculateTax(0m, 0.15m);

        Assert.Equal(0.00m, result);
    }

    [Fact]
    public void CalculateTax_ZeroTaxRate_ReturnsZero()
    {
        var result = FinancialCalculationEngine.CalculateTax(100m, 0m);

        Assert.Equal(0.00m, result);
    }

    [Theory]
    [InlineData(-100.00, 0.15, -15.00)]
    [InlineData(-50.00, 0.05, -2.50)]
    public void CalculateTax_NegativeSubtotal_ReturnsNegativeTax(decimal subtotal, decimal taxRate, decimal expected)
    {
        var result = FinancialCalculationEngine.CalculateTax(subtotal, taxRate);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-50, 40)]
    [InlineData(-100, 0)]
    [InlineData(-0.01, 10)]
    public void CalculateMarginPercentage_SubtotalLessThanOrEqualToZero_ReturnsZero(decimal subtotal, decimal totalCost)
    {
        var result = FinancialCalculationEngine.CalculateMarginPercentage(subtotal, totalCost);

        Assert.Equal(0m, result);
    }

    [Theory]
    [InlineData(100.00, 40.00, 60.00)]
    [InlineData(200.00, 50.00, 75.00)]
    [InlineData(80.00, 20.00, 75.00)]
    public void CalculateMarginPercentage_StandardCalculation_ReturnsExpectedPercentage(decimal subtotal, decimal totalCost, decimal expected)
    {
        var result = FinancialCalculationEngine.CalculateMarginPercentage(subtotal, totalCost);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculateMarginPercentage_TotalCostEqualsSubtotal_ReturnsZero()
    {
        var result = FinancialCalculationEngine.CalculateMarginPercentage(100m, 100m);

        Assert.Equal(0.00m, result);
    }

    [Fact]
    public void CalculateMarginPercentage_TotalCostIsZero_ReturnsOneHundred()
    {
        var result = FinancialCalculationEngine.CalculateMarginPercentage(100m, 0m);

        Assert.Equal(100.00m, result);
    }

    [Theory]
    [InlineData(100.00, 150.00, -50.00)]
    [InlineData(200.00, 300.00, -50.00)]
    [InlineData(50.00, 100.00, -100.00)]
    public void CalculateMarginPercentage_CostExceedsSubtotal_ReturnsNegativeMargin(decimal subtotal, decimal totalCost, decimal expected)
    {
        var result = FinancialCalculationEngine.CalculateMarginPercentage(subtotal, totalCost);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(200.00, 66.89, 66.56)]     // (133.11 / 200) * 100 = 66.555 -> 66.56
    [InlineData(1000.00, 874.55, 12.55)]   // (125.45 / 1000) * 100 = 12.545 -> 12.55
    [InlineData(200.00, 333.11, -66.56)]   // (-133.11 / 200) * 100 = -66.555 -> -66.56
    public void CalculateMarginPercentage_MidpointRounding_RoundsAwayFromZero(decimal subtotal, decimal totalCost, decimal expected)
    {
        var result = FinancialCalculationEngine.CalculateMarginPercentage(subtotal, totalCost);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ActiveCombinedTaxRate_FallsBackToQuebecWhenNullOrEmpty_AndSumsTemplateWhenPresent()
    {
        FinancialCalculationEngine.ActiveTemplate = null;
        Assert.Equal(FinancialCalculationEngine.QuebecCombinedTaxRate, FinancialCalculationEngine.ActiveCombinedTaxRate);

        FinancialCalculationEngine.ActiveTemplate = new JurisdictionTemplate { SalesTaxes = [] };
        Assert.Equal(FinancialCalculationEngine.QuebecCombinedTaxRate, FinancialCalculationEngine.ActiveCombinedTaxRate);

        FinancialCalculationEngine.ActiveTemplate = new JurisdictionTemplate
        {
            JurisdictionId = "CA-ON",
            SalesTaxes =
            [
                new SalesTaxRule { Code = "HST", Name = "HST", Rate = 0.13m }
            ]
        };
        Assert.Equal(0.13m, FinancialCalculationEngine.ActiveCombinedTaxRate);
    }

    [Fact]
    public void CalculateFromSubTotal_QuebecDefault_ComputesExactTpsTvqAndGrandTotal()
    {
        FinancialCalculationEngine.ActiveTemplate = null;

        var result = FinancialCalculationEngine.CalculateFromSubTotal(100.00m);

        Assert.Equal(100.00m, result.SubTotal);
        Assert.Equal(5.00m, result.TpsAmount);
        Assert.Equal(9.98m, result.TvqAmount);
        Assert.Equal(14.98m, result.TaxAmount);
        Assert.Equal(result.TaxAmount, result.TotalTax);
        Assert.Equal(114.98m, result.GrandTotal);
    }

    [Fact]
    public void CalculateFromSubTotal_ZeroOrTaxExempt_ReturnsZeroTax()
    {
        Assert.Equal(new TaxBreakdown(0m, 0m, 0m, 0m, 0m), FinancialCalculationEngine.CalculateFromSubTotal(0m));
        Assert.Equal(new TaxBreakdown(250.00m, 0m, 0m, 0m, 250.00m), FinancialCalculationEngine.CalculateFromSubTotal(250.00m, isTaxExempt: true));
    }

    [Fact]
    public void CalculateFromSubTotal_NonQuebecTemplate_UsesActiveTemplateTaxesIncludingCompound()
    {
        FinancialCalculationEngine.ActiveTemplate = new JurisdictionTemplate
        {
            JurisdictionId = "CA-PE-LEGACY",
            SalesTaxes =
            [
                new SalesTaxRule { Code = "GST", Name = "GST", Rate = 0.05m, IsCompound = false },
                new SalesTaxRule { Code = "PST", Name = "Compound PST", Rate = 0.10m, IsCompound = true }
            ]
        };

        var result = FinancialCalculationEngine.CalculateFromSubTotal(100.00m);

        // GST = 5.00, PST = 10% of (100 + 5.00) = 10.50 -> TotalTax = 15.50
        Assert.Equal(100.00m, result.SubTotal);
        Assert.Equal(5.00m, result.TpsAmount);
        Assert.Equal(10.50m, result.TvqAmount);
        Assert.Equal(15.50m, result.TaxAmount);
        Assert.Equal(115.50m, result.GrandTotal);
    }

    [Fact]
    public void CalculateFromSubTotal_CustomTaxRate_HandlesZeroQuebecSingleTemplateAndCustomRates()
    {
        Assert.Equal(
            new TaxBreakdown(100.00m, 0m, 0m, 0m, 100.00m),
            FinancialCalculationEngine.CalculateFromSubTotal(100.00m, 0m));

        var qcExact = FinancialCalculationEngine.CalculateFromSubTotal(100.00m, FinancialCalculationEngine.QuebecCombinedTaxRate);
        Assert.Equal(5.00m, qcExact.TpsAmount);
        Assert.Equal(9.98m, qcExact.TvqAmount);
        Assert.Equal(114.98m, qcExact.GrandTotal);

        FinancialCalculationEngine.ActiveTemplate = new JurisdictionTemplate
        {
            JurisdictionId = "CA-ON",
            SalesTaxes = [new SalesTaxRule { Code = "HST", Name = "HST", Rate = 0.13m }]
        };
        var singleMatch = FinancialCalculationEngine.CalculateFromSubTotal(200.00m, 0.13m);
        Assert.Equal(26.00m, singleMatch.TpsAmount);
        Assert.Equal(0m, singleMatch.TvqAmount);
        Assert.Equal(26.00m, singleMatch.TaxAmount);
        Assert.Equal(226.00m, singleMatch.GrandTotal);

        // Proportional split when custom rate does not match active template
        var customArbitrary = FinancialCalculationEngine.CalculateFromSubTotal(100.00m, 0.20m);
        Assert.Equal(20.00m, customArbitrary.TaxAmount);
        Assert.Equal(customArbitrary.TaxAmount, customArbitrary.TpsAmount + customArbitrary.TvqAmount);
        Assert.Equal(120.00m, customArbitrary.GrandTotal);
    }

    [Fact]
    public void CalculateFromGrandTotal_QuebecAndNonQuebecTemplates_AlwaysReconcilesToExactCent()
    {
        Assert.Equal(new TaxBreakdown(0m, 0m, 0m, 0m, 0m), FinancialCalculationEngine.CalculateFromGrandTotal(0m));
        Assert.Equal(new TaxBreakdown(100.00m, 0m, 0m, 0m, 100.00m), FinancialCalculationEngine.CalculateFromGrandTotal(100.00m, isTaxExempt: true));

        FinancialCalculationEngine.ActiveTemplate = null;
        var qc = FinancialCalculationEngine.CalculateFromGrandTotal(114.98m);
        Assert.Equal(114.98m, qc.GrandTotal);
        Assert.Equal(qc.GrandTotal, qc.SubTotal + qc.TpsAmount + qc.TvqAmount);
        Assert.Equal(qc.TaxAmount, qc.TpsAmount + qc.TvqAmount);
        Assert.Equal(100.00m, qc.SubTotal);
        Assert.Equal(5.00m, qc.TpsAmount);
        Assert.Equal(9.98m, qc.TvqAmount);

        // Explicit QC tax set takes the QC branch too
        FinancialCalculationEngine.ActiveTemplate = new JurisdictionTemplate
        {
            SalesTaxes =
            [
                new SalesTaxRule { Code = "TPS", Rate = FinancialCalculationEngine.QuebecTpsRate },
                new SalesTaxRule { Code = "TVQ", Rate = FinancialCalculationEngine.QuebecTvqRate }
            ]
        };
        var qcFromTemplate = FinancialCalculationEngine.CalculateFromGrandTotal(114.98m);
        Assert.Equal(qc, qcFromTemplate);

        // Non-QC single & multi-tax templates
        FinancialCalculationEngine.ActiveTemplate = new JurisdictionTemplate
        {
            JurisdictionId = "UK",
            SalesTaxes = [new SalesTaxRule { Code = "VAT", Rate = 0.20m }]
        };
        var uk = FinancialCalculationEngine.CalculateFromGrandTotal(120.00m);
        Assert.Equal(100.00m, uk.SubTotal);
        Assert.Equal(20.00m, uk.TpsAmount);
        Assert.Equal(0m, uk.TvqAmount);
        Assert.Equal(120.00m, uk.GrandTotal);

        FinancialCalculationEngine.ActiveTemplate = new JurisdictionTemplate
        {
            JurisdictionId = "CA-BC",
            SalesTaxes =
            [
                new SalesTaxRule { Code = "GST", Rate = 0.05m },
                new SalesTaxRule { Code = "PST", Rate = 0.07m }
            ]
        };
        var bc = FinancialCalculationEngine.CalculateFromGrandTotal(112.00m);
        Assert.Equal(100.00m, bc.SubTotal);
        Assert.Equal(5.00m, bc.TpsAmount);
        Assert.Equal(7.00m, bc.TvqAmount);
        Assert.Equal(12.00m, bc.TaxAmount);

        FinancialCalculationEngine.ActiveTemplate = new JurisdictionTemplate
        {
            JurisdictionId = "ZERO",
            SalesTaxes = [new SalesTaxRule { Code = "ZERO", Rate = 0m }]
        };
        var zeroRate = FinancialCalculationEngine.CalculateFromGrandTotal(99.95m);
        Assert.Equal(new TaxBreakdown(99.95m, 0m, 0m, 0m, 99.95m), zeroRate);
    }

    [Fact]
    public void BuildTaxComponentsFromSubTotal_ReturnsQuebecDefaultsAndRespectsCompoundAndZeroRules()
    {
        Assert.Empty(FinancialCalculationEngine.BuildTaxComponentsFromSubTotal(0m));
        Assert.Empty(FinancialCalculationEngine.BuildTaxComponentsFromSubTotal(100m, isTaxExempt: true));

        FinancialCalculationEngine.ActiveTemplate = null;
        var qcLines = FinancialCalculationEngine.BuildTaxComponentsFromSubTotal(100.00m);
        Assert.Equal(2, qcLines.Count);
        Assert.Equal("TPS", qcLines[0].Code);
        Assert.Equal(5.00m, qcLines[0].Amount);
        Assert.Equal("2160", qcLines[0].GlAccountCode);
        Assert.Equal("TVQ", qcLines[1].Code);
        Assert.Equal(9.98m, qcLines[1].Amount);
        Assert.Equal("2170", qcLines[1].GlAccountCode);

        var customRules = new[]
        {
            new SalesTaxRule { Code = "SKIP", Name = "Zero", Rate = 0m },
            new SalesTaxRule
            {
                Code = "GST",
                Name = "GST",
                Rate = 0.05m,
                IsRecoverable = true,
                AuthorityName = "CRA",
                CollectedLiabilityGlCode = "2160"
            },
            new SalesTaxRule
            {
                Code = "QST",
                Name = "Compound QST",
                Rate = 0.10m,
                IsCompound = true,
                IsRecoverable = false,
                AuthorityName = "RQ",
                CollectedLiabilityGlCode = "2175"
            }
        };

        var lines = FinancialCalculationEngine.BuildTaxComponentsFromSubTotal(200.00m, customRules);
        Assert.Equal(2, lines.Count);
        Assert.Equal("GST", lines[0].Code);
        Assert.Equal(10.00m, lines[0].Amount);
        Assert.True(lines[0].IsRecoverable);
        Assert.Equal("QST", lines[1].Code);
        Assert.Equal(21.00m, lines[1].Amount); // 10% * (200 + 10)
        Assert.False(lines[1].IsRecoverable);
        Assert.Equal("2175", lines[1].GlAccountCode);
    }

    [Fact]
    public void CalculateMileageAllowance_HandlesZeroFlatAndTwoTierThresholds()
    {
        var rule = new MileageAllowanceRule
        {
            PrimaryRatePerKm = 0.70m,
            TierThresholdKm = 5000m,
            SecondaryRatePerKm = 0.64m
        };

        var zero = FinancialCalculationEngine.CalculateMileageAllowance(0m, rule: rule);
        Assert.Equal(0m, zero.TotalAllowance);
        Assert.Equal(0.70m, zero.EffectiveRatePerKm);

        // Under threshold
        var under = FinancialCalculationEngine.CalculateMileageAllowance(1000m, priorYtdKilometers: 1000m, rule: rule);
        Assert.Equal(700.00m, under.TotalAllowance);
        Assert.Equal(0.7000m, under.EffectiveRatePerKm);

        // Straddling threshold: 200km @ 0.70 ($140) + 300km @ 0.64 ($192) = $332.00; effective = 0.6640
        var split = FinancialCalculationEngine.CalculateMileageAllowance(500m, priorYtdKilometers: 4800m, rule: rule);
        Assert.Equal(332.00m, split.TotalAllowance);
        Assert.Equal(0.6640m, split.EffectiveRatePerKm);

        // Entirely above threshold
        var over = FinancialCalculationEngine.CalculateMileageAllowance(100m, priorYtdKilometers: 6000m, rule: rule);
        Assert.Equal(64.00m, over.TotalAllowance);
        Assert.Equal(0.6400m, over.EffectiveRatePerKm);

        // Flat rate (null threshold) using ActiveTemplate
        FinancialCalculationEngine.ActiveTemplate = new JurisdictionTemplate
        {
            MileageRules = new MileageAllowanceRule { PrimaryRatePerKm = 0.67m, TierThresholdKm = null }
        };
        var flat = FinancialCalculationEngine.CalculateMileageAllowance(150m);
        Assert.Equal(100.50m, flat.TotalAllowance);
        Assert.Equal(0.67m, flat.EffectiveRatePerKm);
    }

    [Fact]
    public void CalculateTpsTvqAndRoundCurrency_RespectTaxExemptAndAwayFromZeroRounding()
    {
        Assert.Equal(5.00m, FinancialCalculationEngine.CalculateTps(100.00m));
        Assert.Equal(0m, FinancialCalculationEngine.CalculateTps(100.00m, isTaxExempt: true));
        Assert.Equal(9.98m, FinancialCalculationEngine.CalculateTvq(100.00m));
        Assert.Equal(0m, FinancialCalculationEngine.CalculateTvq(100.00m, isTaxExempt: true));
        Assert.Equal(10.03m, FinancialCalculationEngine.RoundCurrency(10.025m));
        Assert.Equal(-10.03m, FinancialCalculationEngine.RoundCurrency(-10.025m));
    }

    [Fact]
    public void FinancialDocuments_TaxAmount_DelegatesToFinancialCalculationEngineCalculateTax()
    {
        var invoice = new Invoice
        {
            TaxRate = 0.05555m,
            LineItems = [new InvoiceLine { Quantity = 1m, UnitPrice = 100.00m }]
        };
        Assert.Equal(5.56m, invoice.TaxAmount);

        var poTaxed = new PurchaseOrder
        {
            ApplyTax = true,
            TaxRate = 0.05555m,
            LineItems = [new PoItem { Quantity = 1m, UnitPrice = 100.00m }]
        };
        Assert.Equal(5.56m, poTaxed.TaxAmount);

        var poUntaxed = new PurchaseOrder
        {
            ApplyTax = false,
            TaxRate = 0.14975m,
            LineItems = [new PoItem { Quantity = 1m, UnitPrice = 100.00m }]
        };
        Assert.Equal(0m, poUntaxed.TaxAmount);

        var quoteTaxed = new Quote
        {
            ShowTax = true,
            TaxRate = 0.05555m,
            MaterialLineItems = [new QuoteItemRow { Quantity = 1m, UnitPrice = 100.00m }]
        };
        Assert.Equal(5.56m, quoteTaxed.TaxAmount);

        var quoteUntaxed = new Quote
        {
            ShowTax = false,
            TaxRate = 0.14975m,
            MaterialLineItems = [new QuoteItemRow { Quantity = 1m, UnitPrice = 100.00m }]
        };
        Assert.Equal(0m, quoteUntaxed.TaxAmount);
    }

    [Theory]
    [InlineData("0.14975", "14.975")]
    [InlineData("0.1497500", "14.975")]
    [InlineData("0.05", "5")]
    [InlineData("0.05000", "5")]
    [InlineData("0.09975", "9.975")]
    [InlineData("0.13", "13")]
    [InlineData("0.075", "7.5")]
    [InlineData("0.082555", "8.2555")]
    [InlineData("0", "0")]
    [InlineData("0.00000", "0")]
    [InlineData("-0.00000", "0")]
    public void FormatTaxRatePercent_TrimsTrailingZerosToLastSignificantDigit(string rawTaxRate, string expected)
    {
        var rate = decimal.Parse(rawTaxRate, CultureInfo.InvariantCulture);

        var formatted = FinancialCalculationEngine.FormatTaxRatePercent(rate, CultureInfo.InvariantCulture);

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void FormatTaxRatePercent_RespectsFormatProviderDecimalSeparator()
    {
        var frCa = CultureInfo.GetCultureInfo("fr-CA");

        Assert.Equal("14,975", FinancialCalculationEngine.FormatTaxRatePercent(0.14975m, frCa));
        Assert.Equal("5", FinancialCalculationEngine.FormatTaxRatePercent(0.05000m, frCa));
    }
}
