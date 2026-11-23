namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Data;

public enum AccountingMigrationMode
{
    FreshCutover,
    ExistingWorkspaceDelta
}

public enum AccountingMigrationSourceFormat
{
    AutoDetect,
    Sage50DesktopImp,
    Sage50ExportCsv,
    SageBusinessCloudCsv,
    StandardCsv
}

public static class AccountingMigrationBatchStatus
{
    public const string Applied = "Applied";
    public const string RolledBack = "RolledBack";
}

/// <summary>
/// Persisted audit & rollback manifest for an executed accounting cutover migration.
/// Stores IDs of every newly created GL account, Supplier, Project, Invoice, Bill, JournalEntry,
/// and OpeningYtd PayRun so a cutover can be cleanly verified or rolled back in one click.
/// </summary>
public class AccountingMigrationBatch : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExecutedAtUtc { get; set; } = DateTime.UtcNow;
    public string ExecutedByUserId { get; set; } = string.Empty;
    public string ExecutedByUserName { get; set; } = string.Empty;

    public AccountingMigrationMode Mode { get; set; } = AccountingMigrationMode.FreshCutover;
    public AccountingMigrationSourceFormat DetectedFormat { get; set; } = AccountingMigrationSourceFormat.AutoDetect;
    public DateTime AsOfDate { get; set; }
    public string SourceSummary { get; set; } = string.Empty;
    public string Status { get; set; } = AccountingMigrationBatchStatus.Applied;
    public DateTime? RolledBackAtUtc { get; set; }
    public string? RolledBackByUserName { get; set; }

    public List<string> CreatedGlAccountIds { get; set; } = [];
    public List<string> CreatedSupplierIds { get; set; } = [];
    public List<string> CreatedEmployeeIds { get; set; } = [];
    public List<string> CreatedProjectIds { get; set; } = [];
    public List<string> CreatedInvoiceIds { get; set; } = [];
    public List<string> CreatedBillIds { get; set; } = [];
    public List<string> CreatedJournalEntryIds { get; set; } = [];
    public List<string> CreatedPayRunIds { get; set; } = [];

    public int MatchedSuppliersCount { get; set; }
    public int MatchedClientsCount { get; set; }
    public int MatchedEmployeesCount { get; set; }

    public decimal OpeningTotalDebits { get; set; }
    public decimal OpeningTotalCredits { get; set; }
    public decimal TotalOpenArImported { get; set; }
    public decimal TotalOpenApImported { get; set; }
    public decimal ResidualClearingBalance3900 { get; set; }
}
