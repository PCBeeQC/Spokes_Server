namespace Spokes_Server.Core.Models.HR;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Spokes_Server.Core.Models.Accounting;

public static class PayrollCompensationType
{
    public const string Hourly = "Hourly";
    public const string Salaried = "Salaried";
}

public static class PayFrequencyType
{
    public const string Weekly = "Weekly";           // 52 periods/year
    public const string BiWeekly = "BiWeekly";       // 26 periods/year
    public const string SemiMonthly = "SemiMonthly"; // 24 periods/year
    public const string Monthly = "Monthly";         // 12 periods/year

    public static int GetPeriodsPerYear(string? frequency) => (frequency ?? BiWeekly) switch
    {
        Weekly => 52,
        BiWeekly => 26,
        SemiMonthly => 24,
        Monthly => 12,
        _ => 26
    };
}

public static class VacationPayHandling
{
    public const string AccrueInBank = "AccrueInBank";
    public const string PayOutEachPeriod = "PayOutEachPeriod";
}

/// <summary>
/// Employee payroll configuration embedded inside <see cref="Employee.Payroll"/>.
/// Completely jurisdiction-agnostic: stores dynamic parameter overrides keyed by the active
/// <see cref="JurisdictionTemplate"/>'s declared <see cref="EmployeeRuleParameter.Key"/> values.
/// </summary>
public class EmployeePayrollProfile
{
    public string CompensationType { get; set; } = PayrollCompensationType.Hourly;

    /// <summary>
    /// Annual salary when <see cref="CompensationType"/> is <see cref="PayrollCompensationType.Salaried"/>.
    /// If 0, derived from <c>HourlyRate * WeeklyHours * 52</c>.
    /// </summary>
    public decimal AnnualSalary { get; set; }

    public string PayFrequency { get; set; } = PayFrequencyType.BiWeekly;

    /// <summary>
    /// Statutory vacation rate (e.g. 0.04m for 4%, 0.06m for 6%).
    /// </summary>
    public decimal VacationRate { get; set; } = 0.04m;

    public string VacationHandling { get; set; } = VacationPayHandling.AccrueInBank;

    /// <summary>
    /// Cumulative accrued vacation bank in dollars.
    /// </summary>
    public decimal VacationBankBalanceDollars { get; set; }

    /// <summary>
    /// Dynamic parameter overrides keyed by <see cref="EmployeeRuleParameter.Key"/>.
    /// Missing keys automatically resolve to the active template's <see cref="EmployeeRuleParameter.DefaultValue"/>.
    /// </summary>
    public Dictionary<string, string> ParameterValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set of <see cref="CappedContributionRule.Code"/> codes from which this employee is exempt
    /// (e.g. "EI_QC", "EI_CA", "FUTA" for a controlling shareholder / owner).
    /// </summary>
    public HashSet<string> ExemptContributionCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Custom recurring employee deductions or taxable benefits (e.g. Group Health Insurance, 401(k)/RRSP).
    /// </summary>
    public List<EmployeePayRecurringItem> CustomDeductionsAndBenefits { get; set; } = [];

    public string GetParameterRaw(string? key, IReadOnlyList<EmployeeRuleParameter>? definitions = null, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(key))
            return fallback;

        if (ParameterValues != null &&
            ParameterValues.TryGetValue(key.Trim(), out var stored) &&
            stored != null)
        {
            return stored;
        }

        var def = definitions?.FirstOrDefault(p => string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
        return def?.DefaultValue ?? fallback;
    }

    public string GetParameterString(EmployeeRuleParameter param) =>
        GetParameterRaw(param.Key, [param], param.DefaultValue);

    public decimal GetParameterDecimal(string? key, IReadOnlyList<EmployeeRuleParameter>? definitions = null, decimal fallback = 0m)
    {
        var raw = GetParameterRaw(key, definitions, string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (decimal.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ||
            decimal.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public bool GetParameterBool(string? key, IReadOnlyList<EmployeeRuleParameter>? definitions = null, bool fallback = false)
    {
        var raw = GetParameterRaw(key, definitions, string.Empty);
        return bool.TryParse(raw.Trim(), out var parsed) ? parsed : fallback;
    }

    public void SetParameterValue(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        ParameterValues ??= new(StringComparer.OrdinalIgnoreCase);
        if (value == null)
        {
            ParameterValues.Remove(key.Trim());
        }
        else
        {
            ParameterValues[key.Trim()] = value.Trim();
        }
    }

    public void SetParameterDecimal(string key, decimal value) =>
        SetParameterValue(key, value.ToString(CultureInfo.InvariantCulture));

    public void SetParameterBool(string key, bool value) =>
        SetParameterValue(key, value ? "true" : "false");

    public bool IsExemptFromContribution(CappedContributionRule? rule)
    {
        if (rule == null || string.IsNullOrWhiteSpace(rule.Code) || ExemptContributionCodes == null)
            return false;
        return ExemptContributionCodes.Contains(rule.Code);
    }
}

public class EmployeePayRecurringItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public decimal AmountPerPay { get; set; }
    public bool IsPercentageOfGross { get; set; }
    public decimal Percentage { get; set; }
    public bool IsEmployerContribution { get; set; }
    public string GlAccountCode { get; set; } = "2210";
}
