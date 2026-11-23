namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.HR;

public class EmployerContribution : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is EmployerContribution other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Name { get; set; } = string.Empty; // e.g. "QPP", "Health Insurance"

    // Type of Contribution
    public string Type { get; set; } = EmployerContributionType.Percentage; // Percentage or FixedAmount
    public decimal Value { get; set; } = 0; // The percentage (e.g., 6.4 for 6.4%) or Fixed Amount (e.g. 2080.00/yr)

    // Optional Jurisdiction Statutory Cap & Base Rules
    public string? StatutoryCode { get; set; } // e.g. "QPP", "QPP2", "QPIP", "EI_QC", "FSS", "CNESST", "CNT"
    public decimal BaseMultiplier { get; set; } = 1.0m;
    public decimal AnnualExemption { get; set; }
    public decimal? AnnualFloorEarnings { get; set; }
    public decimal? AnnualMaxInsurableEarnings { get; set; }

    // Scope
    public string Scope { get; set; } = EmployerContributionScope.Global; // Global, Team, or Employee
    public List<string> TargetIds { get; set; } = []; // IDs of Teams or Employees depending on Scope

    public bool IsActive { get; set; } = true;

    public bool AppliesTo(Employee? emp)
    {
        if (!IsActive || emp == null) return false;
        if (!string.IsNullOrWhiteSpace(StatutoryCode) &&
            emp.Payroll?.ExemptContributionCodes != null &&
            emp.Payroll.ExemptContributionCodes.Contains(StatutoryCode))
        {
            return false;
        }

        return Scope switch
        {
            EmployerContributionScope.Global => true,
            EmployerContributionScope.Employee => TargetIds != null && TargetIds.Contains(emp.Id),
            EmployerContributionScope.Team => !string.IsNullOrEmpty(emp.TeamId) && TargetIds != null && TargetIds.Contains(emp.TeamId),
            _ => false
        };
    }

    public decimal CalculateAnnualAmountForEmployee(decimal yearlyGross, Employee? emp = null)
    {
        if (emp != null && !AppliesTo(emp)) return 0m;
        if (yearlyGross <= 0m) return 0m;

        if (Type == EmployerContributionType.FixedAmount)
        {
            return Value;
        }

        if (Type != EmployerContributionType.Percentage || Value <= 0m)
        {
            return 0m;
        }

        var multiplier = BaseMultiplier > 0m ? BaseMultiplier : 1.0m;
        decimal subjectEarnings = yearlyGross * multiplier;
        var floor = AnnualFloorEarnings ?? 0m;

        if (AnnualMaxInsurableEarnings.HasValue && AnnualMaxInsurableEarnings.Value > 0m)
        {
            subjectEarnings = Math.Min(subjectEarnings, AnnualMaxInsurableEarnings.Value);
        }

        if (floor > 0m)
        {
            subjectEarnings = Math.Max(0m, subjectEarnings - floor);
        }

        if (AnnualExemption > 0m)
        {
            subjectEarnings = Math.Max(0m, subjectEarnings - AnnualExemption);
        }

        return subjectEarnings * (Value / 100m);
    }

    public static decimal CalculateAnnualContributions(Employee? emp, IEnumerable<EmployerContribution>? contributions)
    {
        if (emp == null || contributions == null) return 0m;
        var weeklyHours = emp.WeeklyHours > 0m ? emp.WeeklyHours : 40m;
        var yearlyGross = emp.Payroll?.CompensationType == PayrollCompensationType.Salaried && emp.Payroll.AnnualSalary > 0m
            ? emp.Payroll.AnnualSalary
            : emp.HourlyRate * weeklyHours * 52m;

        decimal total = 0m;
        foreach (var c in contributions)
        {
            if (c == null || !c.AppliesTo(emp)) continue;
            total += c.CalculateAnnualAmountForEmployee(yearlyGross, emp);
        }

        return total;
    }

    public static decimal CalculateHourlyContribution(Employee? emp, IEnumerable<EmployerContribution>? contributions)
    {
        if (emp == null || contributions == null) return 0m;
        var weeklyHours = emp.WeeklyHours > 0m ? emp.WeeklyHours : 40m;
        var yearlyHours = weeklyHours * 52m;
        if (yearlyHours <= 0m) return 0m;
        return CalculateAnnualContributions(emp, contributions) / yearlyHours;
    }

    public static decimal CalculateBaseHourlyRate(Employee? emp)
    {
        if (emp == null) return 0m;
        if (emp.HourlyRate > 0m) return emp.HourlyRate;
        var weeklyHours = emp.WeeklyHours > 0m ? emp.WeeklyHours : 40m;
        var yearlyHours = weeklyHours * 52m;
        return emp.Payroll?.AnnualSalary > 0m && yearlyHours > 0m
            ? emp.Payroll.AnnualSalary / yearlyHours
            : 0m;
    }

    public static decimal CalculateTotalHourlyCost(Employee? emp, IEnumerable<EmployerContribution>? contributions)
    {
        if (emp == null) return 0m;
        var baseHourly = CalculateBaseHourlyRate(emp);

        if (baseHourly <= 0m) return 0m;
        return Math.Round(baseHourly + CalculateHourlyContribution(emp, contributions), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Builds standard <see cref="EmployerContribution"/> records from a <see cref="JurisdictionTemplate"/>'s
    /// statutory employer payroll rules and vacation rate so they can be synced into <c>EmployerContributionRepository</c>.
    /// </summary>
    public static List<EmployerContribution> BuildFromJurisdictionTemplate(JurisdictionTemplate template)
    {
        var list = new List<EmployerContribution>();
        if (template?.PayrollRules == null) return list;

        foreach (var rule in template.PayrollRules.Contributions.Where(r => r.EmployerRate > 0m))
        {
            list.Add(new EmployerContribution
            {
                Id = $"stat-{template.JurisdictionId.ToLowerInvariant()}-{rule.Code.ToLowerInvariant()}",
                Name = $"{rule.Code} — {rule.Name}",
                StatutoryCode = rule.Code,
                Type = EmployerContributionType.Percentage,
                Value = Math.Round(rule.EmployerRate * 100m, 4),
                BaseMultiplier = rule.BaseMultiplier > 0m ? rule.BaseMultiplier : 1.0m,
                AnnualExemption = rule.AnnualExemption,
                AnnualFloorEarnings = rule.AnnualFloorEarnings,
                AnnualMaxInsurableEarnings = rule.AnnualMaxInsurableEarnings,
                Scope = EmployerContributionScope.Global,
                IsActive = true
            });
        }

        if (template.PayrollRules.DefaultVacationRate > 0m)
        {
            list.Add(new EmployerContribution
            {
                Id = $"stat-{template.JurisdictionId.ToLowerInvariant()}-vacation",
                Name = $"Vacation Indemnity ({template.PayrollRules.DefaultVacationRate * 100m:0.#}%)",
                StatutoryCode = "VACATION",
                Type = EmployerContributionType.Percentage,
                Value = Math.Round(template.PayrollRules.DefaultVacationRate * 100m, 4),
                Scope = EmployerContributionScope.Global,
                IsActive = true
            });
        }

        return list;
    }
}

public static class EmployerContributionType
{
    public const string Percentage = "Percentage"; // % of Gross Salary
    public const string FixedAmount = "FixedAmount"; // $ per Year
}

public static class EmployerContributionScope
{
    public const string Global = "Global"; // All employees
    public const string Team = "Team"; // Specific Teams
    public const string Employee = "Employee"; // Specific Employees
}
