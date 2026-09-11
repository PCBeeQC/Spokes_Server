namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class ExpenseReport : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (ExpenseReport)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ReportNumber { get; set; } = string.Empty; // e.g. EXP2512-001

    // Employee Info
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;

    // Dates
    public DateTime DateCreated { get; set; } = DateTime.Today;
    public DateTime? DateSubmitted { get; set; }
    public DateTime? DatePaid { get; set; }

    // Content
    public List<ExpenseItem> Items { get; set; } = new();

    // Financials
    // This is a calculated property based on items
    public decimal TotalAmount => Items.Sum(i => i.Total);

    public string Status
    {
        get
        {
            if (DatePaid.HasValue) return ExpenseReportStatus.Paid;
            if (DateSubmitted.HasValue) return ExpenseReportStatus.Submitted;
            return ExpenseReportStatus.Draft;
        }
    }
}

public class ExpenseItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = string.Empty;

    public DateTime Date { get; set; } = DateTime.Today;
    public string Description { get; set; } = string.Empty;

    // Type of Expense
    public bool IsKilometrage { get; set; } = false;

    // For Kilometrage
    public decimal Kilometers { get; set; } = 0;
    public decimal KilometrageRate { get; set; } = 0; // The rate at the time of entry

    // For Receipt
    public string ReceiptPath { get; set; } = string.Empty; // Path to file

    // Amount (If Kilometrage, calc from Km * Rate. If regular, manual entry)
    public decimal Amount { get; set; } = 0;

    public decimal Total => IsKilometrage ? (Kilometers * KilometrageRate) : Amount;
}

public static class ExpenseReportStatus
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Paid = "Paid";
}



