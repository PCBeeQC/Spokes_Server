namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Models.Core;

public enum MigrationFileKind
{
    Auto,
    SageImp,
    TrialBalance,
    ChartOfAccounts,
    Vendors,
    Customers,
    Employees,
    Projects,
    Inventory,
    OpenReceivables,
    OpenPayables,
    PayrollYtd,
    ClockifyTimesheets
}

public enum MigrationGlAction
{
    MapToExisting,
    CreateAccount,
    Ignore
}

public enum MigrationPartyAction
{
    LinkExisting,
    CreateNew,
    Skip
}

public enum MigrationEmployeeAction
{
    LinkExisting,
    CreateActive,
    CreateInactiveFormer,
    Skip
}

public enum MigrationProjectAction
{
    LinkExisting,
    CreateActive,
    CreateCompletedArchived,
    Skip
}

public sealed class MigrationUploadedFile
{
    public string FileName { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
    public MigrationFileKind ExplicitKind { get; set; } = MigrationFileKind.Auto;
}

public sealed class AccountingMigrationPreviewRequest
{
    public AccountingMigrationMode Mode { get; set; } = AccountingMigrationMode.FreshCutover;
    public AccountingMigrationSourceFormat Format { get; set; } = AccountingMigrationSourceFormat.AutoDetect;
    public DateTime AsOfDate { get; set; } = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1);
    public List<MigrationUploadedFile> Files { get; set; } = [];
}

public sealed class GlAccountMappingRow
{
    public string SourceCode { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = GlAccountType.Asset;
    public string StandardTaxCode { get; set; } = string.Empty;

    /// <summary>External ending Debit column (>= 0) as of cutover date.</summary>
    public decimal DebitAmount { get; set; }
    /// <summary>External ending Credit column (>= 0) as of cutover date.</summary>
    public decimal CreditAmount { get; set; }

    /// <summary>Signed balance (Debit - Credit) computed from External TB.</summary>
    public decimal ExternalSignedBalance => Math.Round(DebitAmount - CreditAmount, 2);

    /// <summary>Existing Spokes signed balance (Debit - Credit) as of cutover date (for ExistingWorkspaceDelta mode).</summary>
    public decimal SpokesSignedBalance { get; set; }

    public MigrationGlAction Action { get; set; } = MigrationGlAction.MapToExisting;
    public string? TargetGlCode { get; set; }
    public string SuggestedReason { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public bool IsSystemGeneratedSpokesOnly { get; set; }
    public bool Include { get; set; } = true;
    public string ExternalSubType { get; set; } = string.Empty;
    public string ExternalClassLabel { get; set; } = string.Empty;
    public bool IsLikelyObsolete { get; set; }
}

public sealed class PartyMigrationRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public bool IsCustomer { get; set; }
    public bool IsAlsoClient { get; set; }
    public bool IsAlsoVendor { get; set; }
    public bool Include { get; set; } = true;
    public bool IsFlaggedEmployeeOrTaxAgency { get; set; }
    public string FlagReason { get; set; } = string.Empty;
    public string SourceCode { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string DefaultCurrency { get; set; } = string.Empty;
    public PartyContactInfo ContactInfo { get; set; } = new();

    public MigrationPartyAction Action { get; set; } = MigrationPartyAction.CreateNew;
    public string? MatchedEntityId { get; set; }
    public string? MatchedEntityName { get; set; }
    public decimal MatchScore { get; set; }
    public string MatchReason { get; set; } = string.Empty;
}

public sealed class ProjectMigrationRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RawName { get; set; } = string.Empty;
    public string ExtractedCode { get; set; } = string.Empty;
    public string CleanName { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public string MatchedClientName { get; set; } = string.Empty;
    public string? MatchedSupplierId { get; set; }
    public string? MatchedExistingProjectId { get; set; }
    public string? MatchedExistingProjectName { get; set; }
    public decimal MatchScore { get; set; }
    public string MatchReason { get; set; } = string.Empty;
    public bool IsInternalBucket { get; set; }
    public MigrationProjectAction Action { get; set; } = MigrationProjectAction.CreateActive;
    public bool Include { get; set; } = true;
}

public sealed class OpenSubledgerMigrationRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public bool IsReceivable { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string PartyName { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public DateTime DueDate { get; set; }
    public string Currency { get; set; } = "CAD";
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal OpenAmount { get; set; }
    public string Notes { get; set; } = string.Empty;
    public bool Include { get; set; } = true;

    public string? MatchedPartyId { get; set; }
    public string? MatchedPartyName { get; set; }
    public string? ExistingProjectId { get; set; }
    public string? ExistingDocumentConflict { get; set; }
}

public sealed class PayrollYtdMigrationRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SourceEmployeeName { get; set; } = string.Empty;
    public string? SourceEmail { get; set; }
    public string? MatchedEmployeeId { get; set; }
    public string? MatchedEmployeeName { get; set; }
    public decimal MatchScore { get; set; }
    public string MatchReason { get; set; } = string.Empty;
    public bool Include { get; set; } = true;

    public decimal GrossPayYtd { get; set; }
    public decimal VacationAccruedOrPaidYtd { get; set; }
    public decimal FederalTaxYtd { get; set; }
    public decimal RegionalTaxYtd { get; set; }

    /// <summary>Jurisdiction contribution code -> (EmployeeAmount, EmployerAmount, InsurableEarnings).</summary>
    public Dictionary<string, (decimal Employee, decimal Employer, decimal Insurable)> ContributionsByCode { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Raw common columns preserved cleanly for UI inspection & editing
    public decimal QppCppEmployeeYtd { get; set; }
    public decimal QppCppEmployerYtd { get; set; }
    public decimal Qpp2EmployeeYtd { get; set; }
    public decimal Qpp2EmployerYtd { get; set; }
    public decimal EiEmployeeYtd { get; set; }
    public decimal EiEmployerYtd { get; set; }
    public decimal QpipEmployeeYtd { get; set; }
    public decimal QpipEmployerYtd { get; set; }

    // Employee Directory / Sage 50 Employee fields
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Street1 { get; set; } = string.Empty;
    public string Street2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Province { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string SecondaryPhone { get; set; } = string.Empty;
    public string SocialInsuranceNumber { get; set; } = string.Empty;
    public DateOnly? BirthDate { get; set; }
    public DateOnly? HireDate { get; set; }
    public int PayPeriodsPerYear { get; set; } = 26;
    public bool FromEmployeeDirectoryFile { get; set; }
    public bool HasDuplicateSinWarning { get; set; }
    public string? DuplicateMergedFromName { get; set; }
    public MigrationEmployeeAction Action { get; set; } = MigrationEmployeeAction.LinkExisting;
}

public sealed class AccountingMigrationPreview
{
    public AccountingMigrationMode Mode { get; set; } = AccountingMigrationMode.FreshCutover;
    public AccountingMigrationSourceFormat DetectedFormat { get; set; } = AccountingMigrationSourceFormat.StandardCsv;
    public DateTime AsOfDate { get; set; }
    public string Currency { get; set; } = "CAD";
    public string SourceSummary { get; set; } = string.Empty;

    public List<GlAccountMappingRow> GlMappings { get; set; } = [];
    public List<PartyMigrationRow> Vendors { get; set; } = [];
    public List<PartyMigrationRow> Customers { get; set; } = [];
    public List<ProjectMigrationRow> Projects { get; set; } = [];
    public List<OpenSubledgerMigrationRow> OpenInvoices { get; set; } = [];
    public List<OpenSubledgerMigrationRow> OpenBills { get; set; } = [];
    public List<PayrollYtdMigrationRow> PayrollYtds { get; set; } = [];

    public List<string> InformationalNotices { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> BlockingErrors { get; set; } = [];

    public bool HasExternalTrialBalanceBalances { get; set; }
    public bool RequiresDestructiveDeltaConfirmation { get; set; }
    public bool ConfirmDestructiveDelta { get; set; }
    public decimal TotalExternalDebits { get; set; }
    public decimal TotalExternalCredits { get; set; }
    public decimal TotalDeltaDebits { get; set; }
    public decimal TotalDeltaCredits { get; set; }
    public decimal ExpectedOpenArTotal { get; set; }
    public decimal ExpectedOpenApTotal { get; set; }
    public decimal ProjectedClearing3900Residual { get; set; }
    public bool CanExecute => BlockingErrors.Count == 0 && (!RequiresDestructiveDeltaConfirmation || ConfirmDestructiveDelta);
}

public sealed class AccountingMigrationResult
{
    public AccountingMigrationBatch Batch { get; set; } = new();
    public string? OpeningJournalEntryNumber { get; set; }
    public int GlAccountsCreated { get; set; }
    public int SuppliersCreated { get; set; }
    public int SuppliersMatched { get; set; }
    public int ClientsCreated { get; set; }
    public int ClientsMatched { get; set; }
    public int ProjectsCreated { get; set; }
    public int EmployeesCreated { get; set; }
    public int EmployeesLinked { get; set; }
    public int OpenInvoicesCreated { get; set; }
    public int OpenBillsCreated { get; set; }
    public int PayrollYtdStubsCreated { get; set; }
}
