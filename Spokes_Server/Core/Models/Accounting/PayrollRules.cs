namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Supported data types for template-declared employee payroll parameters.
/// Allows any jurisdiction template to declare the exact employee inputs it needs
/// (e.g., personal tax credit amounts, filing status choices, personalized withholding rates).
/// </summary>
public static class EmployeeRuleParameterType
{
    public const string Currency = "Currency";
    public const string Percent = "Percent";
    public const string Decimal = "Decimal";
    public const string Boolean = "Boolean";
    public const string Choice = "Choice";

    public static readonly string[] All = [Currency, Percent, Decimal, Boolean, Choice];
}

public class EmployeeRuleParameterOption
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string LabelFr { get; set; } = string.Empty;

    public string GetLocalizedLabel()
    {
        var isFrench = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase);
        return isFrench && !string.IsNullOrWhiteSpace(LabelFr) ? LabelFr : Label;
    }
}

/// <summary>
/// Declarative per-employee input parameter defined by a <see cref="JurisdictionTemplate"/>
/// and rendered dynamically in <c>EmployeePayrollProfileDialog</c>.
/// </summary>
public class EmployeeRuleParameter
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string LabelFr { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public string HelpTextFr { get; set; } = string.Empty;
    public string DataType { get; set; } = EmployeeRuleParameterType.Currency;
    public string DefaultValue { get; set; } = "0";
    public string ScopeCode { get; set; } = string.Empty;
    public List<EmployeeRuleParameterOption> Options { get; set; } = [];

    public string GetLocalizedLabel()
    {
        var isFrench = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase);
        return isFrench && !string.IsNullOrWhiteSpace(LabelFr) ? LabelFr : Label;
    }

    public string GetLocalizedHelpText()
    {
        var isFrench = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase);
        return isFrench && !string.IsNullOrWhiteSpace(HelpTextFr) ? HelpTextFr : HelpText;
    }
}

/// <summary>
/// Declarative payroll calculation rules embedded inside a <see cref="JurisdictionTemplate"/>.
/// Built from pure composable primitives: employee parameters, statutory contributions with base multipliers and caps,
/// and income tax calculation pipelines.
/// </summary>
public class JurisdictionPayrollRules
{
    public decimal DefaultVacationRate { get; set; } = 0.04m;
    public decimal SeniorVacationRate { get; set; } = 0.06m;
    public int SeniorityYearsForEnhancedVacation { get; set; } = 3;

    public List<EmployeeRuleParameter> EmployeeParameters { get; set; } = [];
    public List<CappedContributionRule> Contributions { get; set; } = [];
    public List<IncomeTaxJurisdictionTable> IncomeTaxTables { get; set; } = [];
}

/// <summary>
/// Represents a statutory social contribution or employer payroll levy
/// (e.g. RRQ/QPP, RQAP/QPIP, AE/EI, FSS, CNESST, CNT, Social Security OASDI, Medicare, FUTA, URSSAF, CSG/CRDS).
/// </summary>
public class CappedContributionRule
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Scalar applied to gross earnings before checking floor/ceiling/exemption (e.g. 1.0 = 100%, 0.9825 = 98.25% for France CSG/CRDS).
    /// </summary>
    public decimal BaseMultiplier { get; set; } = 1.0m;

    public decimal AnnualExemption { get; set; }
    public decimal? AnnualFloorEarnings { get; set; }
    public decimal? AnnualMaxInsurableEarnings { get; set; }
    public bool ApplyAnnualCap { get; set; } = true;

    public decimal EmployeeRate { get; set; }
    public decimal EmployerRate { get; set; }

    /// <summary>
    /// Optional employee parameter key providing a custom rate percentage (e.g. company-specific workers' compensation rate).
    /// </summary>
    public string? RateOverrideParameterKey { get; set; }

    /// <summary>
    /// True if 100% of the employee contribution reduces taxable income before bracket evaluation when a tax table does not specify an explicit list.
    /// </summary>
    public bool IsTaxDeductibleFromGross { get; set; }

    /// <summary>
    /// Optional portion of <see cref="EmployeeRate"/> that is deductible from gross taxable income before brackets
    /// (e.g. 0.01m for the 1.00% enhanced portion of Canada CPP 5.95% or Québec QPP 6.30%), while the remaining
    /// base rate (<c>EmployeeRate - DeductibleEmployeeRate</c>) remains eligible for <see cref="GrantsBasicTaxCredit"/>.
    /// </summary>
    public decimal DeductibleEmployeeRate { get; set; }

    /// <summary>
    /// True if employee contribution (or its non-deductible base portion when <see cref="DeductibleEmployeeRate"/> &gt; 0)
    /// is eligible for non-refundable tax credit steps that enable <c>IncludeCreditableContributions</c>.
    /// </summary>
    public bool GrantsBasicTaxCredit { get; set; }

    public string SlipBoxCode { get; set; } = string.Empty;
    public string LiabilityGlCode { get; set; } = "2210";
    public string ExpenseGlCode { get; set; } = "5420";

    public decimal GetDeductibleEmployeeAmount(decimal employeeContribution)
    {
        if (employeeContribution <= 0m) return 0m;
        if (IsTaxDeductibleFromGross) return employeeContribution;
        if (DeductibleEmployeeRate > 0m && EmployeeRate > 0m)
        {
            var share = Math.Clamp(DeductibleEmployeeRate / EmployeeRate, 0m, 1m);
            return employeeContribution * share;
        }
        return 0m;
    }

    public decimal GetCreditableEmployeeAmount(decimal employeeContribution)
    {
        if (employeeContribution <= 0m || !GrantsBasicTaxCredit || IsTaxDeductibleFromGross) return 0m;
        if (DeductibleEmployeeRate > 0m && EmployeeRate > 0m)
        {
            var deductibleShare = Math.Clamp(DeductibleEmployeeRate / EmployeeRate, 0m, 1m);
            return employeeContribution * (1m - deductibleShare);
        }
        return employeeContribution;
    }
}

public static class TaxComputationMethod
{
    public const string ProgressiveBrackets = "ProgressiveBrackets";
    public const string BracketSetByParameter = "BracketSetByParameter";
    public const string ParameterRateOrBracketFallback = "ParameterRateOrBracketFallback";
    public const string FlatRate = "FlatRate";

    public static readonly string[] All =
    [
        ProgressiveBrackets,
        BracketSetByParameter,
        ParameterRateOrBracketFallback,
        FlatRate
    ];
}

public static class TaxAdjustmentStepType
{
    /// <summary>
    /// Subtracts <c>(EmployeeParam(ParameterKey, DefaultAmount) + SumOfEligibleContributions) * RateOrFactor</c> from annual tax.
    /// </summary>
    public const string SubtractNonRefundableCredit = "SubtractNonRefundableCredit";

    /// <summary>
    /// Multiplies running annual tax by <c>(1 + RateOrFactor)</c> (e.g. -0.165 for 16.5% Québec Abatement, +0.055 for 5.5% Surtax).
    /// </summary>
    public const string MultiplyByFactor = "MultiplyByFactor";

    /// <summary>
    /// Adds per-period voluntary withholding from <c>EmployeeParam(ParameterKey)</c> after clamping period tax >= 0.
    /// </summary>
    public const string AddFlatParameter = "AddFlatParameter";

    public static readonly string[] All =
    [
        SubtractNonRefundableCredit,
        MultiplyByFactor,
        AddFlatParameter
    ];
}

public class TaxBracketSet
{
    public string OptionKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public decimal StandardDeduction { get; set; }
    public List<TaxBracketRule> Brackets { get; set; } = [];
}

public class TaxAdjustmentStep
{
    public string StepType { get; set; } = TaxAdjustmentStepType.SubtractNonRefundableCredit;
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Decimal rate or factor: e.g. 0.14 (14% credit rate), -0.165 (-16.5% abatement), +0.055 (+5.5% surtax).
    /// </summary>
    public decimal RateOrFactor { get; set; }

    public string? ParameterKey { get; set; }
    public decimal DefaultAmount { get; set; }

    /// <summary>
    /// When true, caps the credit base amount at annualized gross employment income (e.g. CRA Canada Employment Amount CEA = min(AnnualIncome, $1,501)).
    /// </summary>
    public bool CapAtAnnualGrossIncome { get; set; }

    /// <summary>
    /// Optional high-income phase-out floor for default personal credit amounts (e.g. CRA Federal BPA phases from $16,452 down to $14,829 between $181,440 and $258,482).
    /// </summary>
    public decimal? MinimumAmountAfterPhaseOut { get; set; }
    public decimal? PhaseOutStartIncome { get; set; }
    public decimal? PhaseOutEndIncome { get; set; }

    public bool IncludeCreditableContributions { get; set; }
    public List<string> IncludedContributionCodes { get; set; } = [];
}

/// <summary>
/// Composable income tax calculation rule for any tax authority (Federal, Provincial, State, Regional, Municipal).
/// </summary>
public class IncomeTaxJurisdictionTable
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string Authority { get; set; } = string.Empty;

    public string Method { get; set; } = TaxComputationMethod.ProgressiveBrackets;
    public decimal FlatRate { get; set; }
    public string? RateOverrideParameterKey { get; set; }
    public string? BracketSetSelectorParameterKey { get; set; }
    public List<TaxBracketSet> BracketSets { get; set; } = [];

    /// <summary>
    /// Multiplier applied to gross pay before pre-bracket deductions (default 1.0; e.g. 0.90 for a standard 10% expense abatement).
    /// </summary>
    public decimal BaseMultiplier { get; set; } = 1.0m;

    /// <summary>
    /// Annual standard deduction subtracted before evaluating brackets or flat rate.
    /// </summary>
    public decimal PreBracketStandardDeduction { get; set; }

    /// <summary>
    /// Optional percentage of annual gross employment income deducted before brackets (e.g. 0.06 for Revenu Québec 6% Deduction for Workers),
    /// optionally capped at <see cref="PreBracketPercentageDeductionCap"/> (e.g. $1,450 in 2026).
    /// </summary>
    public decimal PreBracketPercentageDeductionRate { get; set; }
    public decimal? PreBracketPercentageDeductionCap { get; set; }

    /// <summary>
    /// Optional employee parameter key supplying additional annual pre-bracket deductions (e.g. W-4 Line 4b).
    /// </summary>
    public string? PreBracketDeductionParameterKey { get; set; }

    /// <summary>
    /// Optional explicit list of contribution codes to subtract before evaluating tax; when empty, uses rules where <c>IsTaxDeductibleFromGross</c> is true.
    /// </summary>
    public List<string> SubtractContributionCodes { get; set; } = [];

    public List<TaxBracketRule> Brackets { get; set; } = [];
    public List<TaxAdjustmentStep> PostSteps { get; set; } = [];

    public string SlipBoxCode { get; set; } = string.Empty;
    public string LiabilityGlCode { get; set; } = "2210";
}

public class TaxBracketRule
{
    public decimal? UpToAnnualIncome { get; set; }
    public decimal Rate { get; set; }
}
