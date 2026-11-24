namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Security;

public interface IAccountingMigrationService
{
    AccountingMigrationPreview BuildPreview(AccountingMigrationPreviewRequest request, Employee? executingEmployee = null);
    void RecalculatePreviewTotals(AccountingMigrationPreview preview);
    AccountingMigrationResult ExecuteMigration(AccountingMigrationPreview preview, string userId, string userName, Employee? executingEmployee = null);
    void RollbackMigrationBatch(string batchId, string userName, Employee? executingEmployee = null);
    List<AccountingMigrationBatch> GetMigrationHistory();
}

/// <summary>
/// Universal Accounting Cutover &amp; Migration Engine supporting Fresh Cutover and Existing Workspace Delta modes,
/// automatic GL/Party/Employee matching, subledger clearing via Opening Balance Equity (3900),
/// mid-year payroll YTD seeding, and deterministic 1-click batch rollback.
/// </summary>
public class AccountingMigrationService : IAccountingMigrationService
{
    private readonly AccountingContext _accounting;
    private readonly ProjectsContext? _projects;
    private readonly HrContext? _hr;
    private readonly ILedgerService _ledger;
    private readonly IJurisdictionTemplateService? _templates;
    private readonly SequenceService _sequences;
    private readonly ILogger<AccountingMigrationService>? _logger;

    public AccountingMigrationService(
        AccountingContext accounting,
        ILedgerService ledger,
        SequenceService sequences,
        ProjectsContext? projects = null,
        HrContext? hr = null,
        IJurisdictionTemplateService? templates = null,
        ILogger<AccountingMigrationService>? logger = null)
    {
        _accounting = accounting ?? throw new ArgumentNullException(nameof(accounting));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _sequences = sequences ?? throw new ArgumentNullException(nameof(sequences));
        _projects = projects;
        _hr = hr;
        _templates = templates;
        _logger = logger;
    }

    public AccountingMigrationPreview BuildPreview(AccountingMigrationPreviewRequest request, Employee? executingEmployee = null)
    {
        DomainActorGuard.RequireEffectiveAdmin(executingEmployee, "build an accounting migration preview");
        ArgumentNullException.ThrowIfNull(request);

        var config = _accounting.GetAccountingConfig();
        var defaultCurrency = !string.IsNullOrWhiteSpace(config.DefaultCurrency) ? config.DefaultCurrency : "CAD";
        var asOf = request.AsOfDate.Date;

        var parsed = AccountingMigrationParser.ParseFiles(request.Files, request.Format, asOf, defaultCurrency);
        var spokesAccounts = _accounting.GlAccounts?.GetAll().OrderBy(a => a.Code).ToList() ?? [];
        var existingSuppliers = _accounting.Suppliers?.GetAll().ToList() ?? [];
        var existingProjects = _projects?.Projects.GetAll().ToList() ?? [];
        var existingEmployees = _hr?.Employees.GetAll().ToList() ?? [];
        var existingInvoices = _accounting.Invoices?.GetAll().ToList() ?? [];
        var existingBills = _accounting.Bills?.GetAll().ToList() ?? [];

        var preview = new AccountingMigrationPreview
        {
            Mode = request.Mode,
            DetectedFormat = parsed.DetectedFormat,
            AsOfDate = asOf,
            Currency = defaultCurrency,
            SourceSummary = string.Join(", ", request.Files.Select(f => f.FileName).Where(n => !string.IsNullOrWhiteSpace(n)))
        };
        preview.InformationalNotices.AddRange(parsed.InformationalNotices);
        preview.Warnings.AddRange(parsed.Warnings);

        // 1. Compute Spokes TB as of Cutover Date for Delta mode (and baseline visibility)
        var (spokesTb, spokesSignedByCode) = BuildSpokesSignedBalanceMap(asOf);
        bool hasExternalTbBalances = parsed.GlRows.Any(r => r.Debit != 0m || r.Credit != 0m);
        preview.HasExternalTrialBalanceBalances = hasExternalTbBalances;

        if (parsed.GlRows.Count > 0 && !hasExternalTbBalances)
        {
            preview.InformationalNotices.Add("Master Data Only (Chart of Accounts) — no non-zero external Trial Balance balances were found; existing Spokes General Ledger balances will be preserved and no Opening Balance journal entry will be posted.");
        }

        if (request.Mode == AccountingMigrationMode.FreshCutover &&
            spokesTb.Rows.Any(r => r.EndingBalance != 0m) &&
            hasExternalTbBalances)
        {
            preview.Warnings.Add("Spokes already contains non-zero General Ledger balances. Consider switching to 'Existing Workspace Delta' mode to avoid duplicating previously posted Spokes activity.");
        }

        // 2. Match / Suggest Chart of Accounts & Trial Balance Rows
        var matchedTargetCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in parsed.GlRows)
        {
            var suggestion = PartyMatchingHelper.SuggestGlAccount(row.Code, row.Name, row.StandardTaxCode, spokesAccounts);
            var action = !string.IsNullOrWhiteSpace(suggestion.SuggestedCode)
                ? MigrationGlAction.MapToExisting
                : MigrationGlAction.CreateAccount;
            if (row.IsLikelyObsolete && row.Debit == 0m && row.Credit == 0m)
            {
                action = MigrationGlAction.Ignore;
            }

            if (!string.IsNullOrWhiteSpace(suggestion.SuggestedCode))
            {
                matchedTargetCodes.Add(suggestion.SuggestedCode);
            }

            var targetCode = suggestion.SuggestedCode ?? row.Code;
            spokesSignedByCode.TryGetValue(targetCode, out var spokesSigned);

            preview.GlMappings.Add(new GlAccountMappingRow
            {
                SourceCode = row.Code,
                SourceName = row.Name,
                SourceType = row.AccountType,
                ExternalSubType = row.SubType,
                ExternalClassLabel = row.ClassLabel,
                IsLikelyObsolete = row.IsLikelyObsolete,
                Include = !(row.IsLikelyObsolete && row.Debit == 0m && row.Credit == 0m),
                StandardTaxCode = row.StandardTaxCode,
                DebitAmount = row.Debit,
                CreditAmount = row.Credit,
                SpokesSignedBalance = Math.Round(spokesSigned, 2),
                Action = action,
                TargetGlCode = suggestion.SuggestedCode,
                SuggestedReason = row.IsLikelyObsolete ? "Obsolete placeholder / legacy asset — defaulted to Skip" : suggestion.Reason,
                Confidence = suggestion.Confidence
            });
        }

        // In Delta mode, include any non-zero Spokes accounts absent from the external file ONLY when an actual Trial Balance with balances was uploaded.
        if (request.Mode == AccountingMigrationMode.ExistingWorkspaceDelta && hasExternalTbBalances)
        {
            foreach (var tbRow in spokesTb.Rows)
            {
                if (matchedTargetCodes.Contains(tbRow.AccountCode)) continue;
                var signed = tbRow.IsDebitNormal ? tbRow.EndingBalance : -tbRow.EndingBalance;
                if (signed == 0m) continue;

                preview.GlMappings.Add(new GlAccountMappingRow
                {
                    SourceCode = tbRow.AccountCode,
                    SourceName = $"{tbRow.AccountName} (Spokes Only)",
                    SourceType = tbRow.AccountType,
                    StandardTaxCode = tbRow.StandardTaxCode,
                    DebitAmount = 0m,
                    CreditAmount = 0m,
                    SpokesSignedBalance = Math.Round(signed, 2),
                    Action = MigrationGlAction.MapToExisting,
                    TargetGlCode = tbRow.AccountCode,
                    SuggestedReason = "Present in Spokes TB, missing in external TB (delta reversal/reclass)",
                    Confidence = 1.00m,
                    IsSystemGeneratedSpokesOnly = true
                });
            }
        }

        // Build lookup of known employee names (both parsed from Sage Employees and existing in HR)
        var employeeNameKeys = existingEmployees
            .Select(e => PartyMatchingHelper.NormalizeTokenSortedName(e.FullName))
            .Concat(parsed.PayrollYtds.Select(p => PartyMatchingHelper.NormalizeTokenSortedName(p.EmployeeName)))
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 3. Match Vendors & Customers -> Unified Address Book (Db.Suppliers), flagging duplicates, Employees & Tax Agencies
        var customerNormNames = parsed.Customers
            .Select(c => PartyMatchingHelper.NormalizeCompanyName(c.ContactInfo.BusinessName))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vendorNormNames = parsed.Vendors
            .Select(v => PartyMatchingHelper.NormalizeCompanyName(v.ContactInfo.BusinessName))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var vendorMatchLookup = new Dictionary<string, (string? Id, string? Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in parsed.Vendors)
        {
            var match = PartyMatchingHelper.FindBestPartyMatch(
                new PartyMatchCandidate(v.ContactInfo.BusinessName, v.ContactInfo.ContactPersonEmail, v.ContactInfo.ContactPersonPhone, v.ContactInfo.TaxId, v.ContactInfo.BusinessAddressZip),
                existingSuppliers,
                s => s.ContactInfo,
                s => s.Name);

            var normCompany = PartyMatchingHelper.NormalizeCompanyName(v.ContactInfo.BusinessName);
            var normPerson = PartyMatchingHelper.NormalizeTokenSortedName(v.ContactInfo.BusinessName);
            bool matchesEmployee = !string.IsNullOrEmpty(normPerson) && employeeNameKeys.Contains(normPerson);
            bool isTaxAgency = PartyMatchingHelper.IsTaxAuthorityOrGovernmentVendor(v.ContactInfo.BusinessName);
            bool flagged = matchesEmployee || isTaxAgency;
            string flagReason = matchesEmployee
                ? "Matches employee (expense reimbursement vendor in Sage)"
                : (isTaxAgency ? "Government tax authority" : string.Empty);

            var row = new PartyMigrationRow
            {
                IsCustomer = false,
                IsAlsoVendor = true,
                IsAlsoClient = !string.IsNullOrEmpty(normCompany) && customerNormNames.Contains(normCompany),
                IsFlaggedEmployeeOrTaxAgency = flagged,
                FlagReason = flagReason,
                Include = !flagged,
                SourceCode = v.SourceCode,
                Website = v.Website,
                DefaultCurrency = v.DefaultCurrency,
                ContactInfo = v.ContactInfo,
                Action = flagged ? MigrationPartyAction.Skip : (match != null ? MigrationPartyAction.LinkExisting : MigrationPartyAction.CreateNew),
                MatchedEntityId = match?.MatchedEntity.Id,
                MatchedEntityName = match?.MatchedEntity.Name,
                MatchScore = match?.Score ?? 0m,
                MatchReason = flagged ? flagReason : (match?.MatchReason ?? "New supplier (Address Book)")
            };
            preview.Vendors.Add(row);

            if (!string.IsNullOrEmpty(normCompany))
            {
                vendorMatchLookup[normCompany] = (row.MatchedEntityId, row.MatchedEntityName ?? v.ContactInfo.BusinessName);
            }
        }

        // 4. Match Customers -> Unified Address Book (Db.Suppliers) rather than fake container projects
        var customerMatchLookup = new Dictionary<string, (string? SupplierId, string? Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in parsed.Customers)
        {
            var match = PartyMatchingHelper.FindBestPartyMatch(
                new PartyMatchCandidate(c.ContactInfo.BusinessName, c.ContactInfo.ContactPersonEmail, c.ContactInfo.ContactPersonPhone, c.ContactInfo.TaxId, c.ContactInfo.BusinessAddressZip),
                existingSuppliers,
                s => s.ContactInfo,
                s => s.Name);

            var normCompany = PartyMatchingHelper.NormalizeCompanyName(c.ContactInfo.BusinessName);
            var isAlsoVendor = !string.IsNullOrEmpty(normCompany) && vendorNormNames.Contains(normCompany);

            var row = new PartyMigrationRow
            {
                IsCustomer = true,
                IsAlsoClient = true,
                IsAlsoVendor = isAlsoVendor,
                Include = true,
                SourceCode = c.SourceCode,
                Website = c.Website,
                DefaultCurrency = c.DefaultCurrency,
                ContactInfo = c.ContactInfo,
                Action = match != null ? MigrationPartyAction.LinkExisting : MigrationPartyAction.CreateNew,
                MatchedEntityId = match?.MatchedEntity.Id,
                MatchedEntityName = match?.MatchedEntity.Name,
                MatchScore = match?.Score ?? 0m,
                MatchReason = match?.MatchReason ?? (isAlsoVendor ? "Shared Client & Supplier (Address Book)" : "New client (Address Book)")
            };
            preview.Customers.Add(row);

            if (!string.IsNullOrEmpty(normCompany))
            {
                customerMatchLookup[normCompany] = (row.MatchedEntityId, row.MatchedEntityName ?? c.ContactInfo.BusinessName);
            }
        }

        // 4B. Match Parsed Projects -> Real Projects linked to Clients (with Year Cutoff defaults)
        var activeCutoffYear = asOf.Year - 1; // e.g., 2024+ stays active around 2025/2026 cutover
        foreach (var proj in parsed.Projects)
        {
            var existingMatch = existingProjects.FirstOrDefault(p =>
                (!string.IsNullOrWhiteSpace(proj.ExtractedCode) &&
                 string.Equals(p.ProjectNumber.Trim(), proj.ExtractedCode.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(PartyMatchingHelper.NormalizeCompanyName(p.Name), PartyMatchingHelper.NormalizeCompanyName(proj.CleanName), StringComparison.OrdinalIgnoreCase));

            var (clientSupId, clientName) = PartyMatchingHelper.MatchProjectToClient(
                proj.RawName,
                proj.ExtractedCode,
                preview.Customers,
                existingSuppliers);

            MigrationProjectAction projAction;
            string reason;
            if (existingMatch != null)
            {
                projAction = MigrationProjectAction.LinkExisting;
                reason = $"Matched existing project ({existingMatch.ProjectNumber})";
            }
            else if (proj.IsInternalBucket)
            {
                projAction = MigrationProjectAction.Skip;
                reason = "Internal overhead/vacation bucket — defaulted to Skip";
            }
            else if (proj.StartDate.HasValue && proj.StartDate.Value.Year < activeCutoffYear)
            {
                projAction = MigrationProjectAction.CreateCompletedArchived;
                reason = $"Started before {activeCutoffYear} — defaulted to Completed/Archived";
            }
            else
            {
                projAction = MigrationProjectAction.CreateActive;
                reason = "Recent project — defaulted to Active";
            }

            preview.Projects.Add(new ProjectMigrationRow
            {
                RawName = proj.RawName,
                ExtractedCode = proj.ExtractedCode,
                CleanName = proj.CleanName,
                StartDate = proj.StartDate,
                MatchedClientName = clientName ?? string.Empty,
                MatchedSupplierId = clientSupId,
                MatchedExistingProjectId = existingMatch?.Id,
                MatchedExistingProjectName = existingMatch?.DisplayName,
                MatchScore = existingMatch != null ? 0.95m : (clientName != null ? 0.85m : 0.50m),
                MatchReason = reason,
                IsInternalBucket = proj.IsInternalBucket,
                Action = projAction,
                Include = projAction != MigrationProjectAction.Skip
            });
        }

        // 5. Open AR Invoices (prefer matching existing/parsed Projects, fall back to Address Book Client)
        var projectsWithClient = existingProjects.Where(p => !string.IsNullOrWhiteSpace(p.Client?.BusinessName)).ToList();
        foreach (var inv in parsed.OpenInvoices)
        {
            var normParty = PartyMatchingHelper.NormalizeCompanyName(inv.PartyName);
            string? matchedProjId = null;
            string? matchedProjName = null;

            var directProjMatch = PartyMatchingHelper.FindBestPartyMatch(
                new PartyMatchCandidate(inv.PartyName),
                projectsWithClient,
                p => p.Client,
                p => p.Name);
            if (directProjMatch != null)
            {
                matchedProjId = directProjMatch.MatchedEntity.Id;
                matchedProjName = directProjMatch.MatchedEntity.Client?.BusinessName ?? directProjMatch.MatchedEntity.Name;
            }
            else if (customerMatchLookup.TryGetValue(normParty, out var foundCust))
            {
                matchedProjName = foundCust.Name;
            }

            var dupInv = !string.IsNullOrWhiteSpace(inv.DocumentNumber)
                ? existingInvoices.FirstOrDefault(x => string.Equals(x.InvoiceNumber, inv.DocumentNumber, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Status, InvoiceStatus.Void, StringComparison.OrdinalIgnoreCase))
                : null;

            preview.OpenInvoices.Add(new OpenSubledgerMigrationRow
            {
                IsReceivable = true,
                DocumentNumber = inv.DocumentNumber,
                PartyName = inv.PartyName,
                IssueDate = inv.IssueDate,
                DueDate = inv.DueDate,
                Currency = inv.Currency,
                SubTotal = inv.SubTotal,
                TaxAmount = inv.TaxAmount,
                OpenAmount = inv.OpenAmount,
                Notes = inv.Notes,
                Include = dupInv == null,
                ExistingProjectId = matchedProjId,
                MatchedPartyId = matchedProjId,
                MatchedPartyName = matchedProjName,
                ExistingDocumentConflict = dupInv != null ? $"Already in Spokes ({dupInv.InvoiceNumber}, Status: {dupInv.Status})" : null
            });
        }

        // 6. Open AP Bills
        foreach (var bill in parsed.OpenBills)
        {
            var normParty = PartyMatchingHelper.NormalizeCompanyName(bill.PartyName);
            string? matchedSupId = null;
            string? matchedSupName = null;
            if (vendorMatchLookup.TryGetValue(normParty, out var foundVendor))
            {
                matchedSupId = foundVendor.Id;
                matchedSupName = foundVendor.Name;
            }
            else
            {
                var directMatch = PartyMatchingHelper.FindBestPartyMatch(
                    new PartyMatchCandidate(bill.PartyName),
                    existingSuppliers,
                    s => s.ContactInfo,
                    s => s.Name);
                if (directMatch != null)
                {
                    matchedSupId = directMatch.MatchedEntity.Id;
                    matchedSupName = directMatch.MatchedEntity.Name;
                }
            }

            var dupBill = !string.IsNullOrWhiteSpace(bill.DocumentNumber)
                ? existingBills.FirstOrDefault(x => !x.IsVoid && string.Equals(x.InvoiceNumber, bill.DocumentNumber, StringComparison.OrdinalIgnoreCase))
                : null;

            preview.OpenBills.Add(new OpenSubledgerMigrationRow
            {
                IsReceivable = false,
                DocumentNumber = bill.DocumentNumber,
                PartyName = bill.PartyName,
                IssueDate = bill.IssueDate,
                DueDate = bill.DueDate,
                Currency = bill.Currency,
                SubTotal = bill.SubTotal,
                TaxAmount = bill.TaxAmount,
                OpenAmount = bill.OpenAmount,
                Notes = bill.Notes,
                Include = dupBill == null,
                MatchedPartyId = matchedSupId,
                MatchedPartyName = matchedSupName,
                ExistingDocumentConflict = dupBill != null ? $"Already in Spokes ({dupBill.InvoiceNumber}, Status: {dupBill.Status})" : null
            });
        }

        // 7. Match Employees & Payroll YTD (from Sage Employees directory and/or Payroll YTD CSV)
        foreach (var pay in parsed.PayrollYtds)
        {
            Employee? sinMatch = null;
            if (!string.IsNullOrWhiteSpace(pay.SocialInsuranceNumber))
            {
                sinMatch = existingEmployees.FirstOrDefault(e =>
                    !string.IsNullOrWhiteSpace(e.SocialInsuranceNumber) &&
                    PartyMatchingHelper.NormalizeIdentifier(e.SocialInsuranceNumber) == pay.SocialInsuranceNumber);
            }

            var empMatch = sinMatch != null
                ? new PartyMatchResult<Employee>(sinMatch, 1.00m, "SIN exact match")
                : PartyMatchingHelper.FindBestEmployeeMatch(pay.EmployeeName, pay.Email, existingEmployees);

            if (empMatch == null)
            {
                var tokenKey = PartyMatchingHelper.NormalizeTokenSortedName(pay.EmployeeName);
                var tokenHit = existingEmployees.FirstOrDefault(e =>
                    PartyMatchingHelper.NormalizeTokenSortedName(e.FullName) == tokenKey);
                if (tokenHit != null)
                    empMatch = new PartyMatchResult<Employee>(tokenHit, 0.94m, "Token-sorted name match");
            }

            var action = empMatch != null
                ? MigrationEmployeeAction.LinkExisting
                : (pay.FromEmployeeDirectoryFile ? MigrationEmployeeAction.CreateActive : MigrationEmployeeAction.LinkExisting);
            var include = empMatch != null || pay.FromEmployeeDirectoryFile;

            preview.PayrollYtds.Add(new PayrollYtdMigrationRow
            {
                SourceEmployeeName = pay.EmployeeName,
                FirstName = pay.FirstName,
                LastName = pay.LastName,
                SourceEmail = pay.Email,
                Street1 = pay.Street1,
                Street2 = pay.Street2,
                City = pay.City,
                Province = pay.Province,
                PostalCode = pay.PostalCode,
                PhoneNumber = pay.PhoneNumber,
                SecondaryPhone = pay.SecondaryPhone,
                SocialInsuranceNumber = pay.SocialInsuranceNumber,
                BirthDate = pay.BirthDate,
                HireDate = pay.HireDate,
                PayPeriodsPerYear = pay.PayPeriodsPerYear,
                FromEmployeeDirectoryFile = pay.FromEmployeeDirectoryFile,
                HasDuplicateSinWarning = pay.HasDuplicateSinWarning,
                DuplicateMergedFromName = pay.DuplicateMergedFromName,
                MatchedEmployeeId = empMatch?.MatchedEntity.Id,
                MatchedEmployeeName = empMatch?.MatchedEntity.FullName,
                MatchScore = empMatch?.Score ?? 0m,
                MatchReason = empMatch?.MatchReason ?? (pay.FromEmployeeDirectoryFile ? "New employee from Sage export" : "No employee match — select employee"),
                Action = action,
                Include = include,
                GrossPayYtd = pay.GrossPay,
                VacationAccruedOrPaidYtd = pay.Vacation,
                FederalTaxYtd = pay.FederalTax,
                RegionalTaxYtd = pay.RegionalTax,
                QppCppEmployeeYtd = pay.QppCppEmployee,
                QppCppEmployerYtd = pay.QppCppEmployer,
                Qpp2EmployeeYtd = pay.Qpp2Employee,
                Qpp2EmployerYtd = pay.Qpp2Employer,
                EiEmployeeYtd = pay.EiEmployee,
                EiEmployerYtd = pay.EiEmployer,
                QpipEmployeeYtd = pay.QpipEmployee,
                QpipEmployerYtd = pay.QpipEmployer
            });
        }

        RecalculatePreviewTotals(preview);
        return preview;
    }

    private (TrialBalanceReport TrialBalance, Dictionary<string, decimal> SignedByCode) BuildSpokesSignedBalanceMap(DateTime asOfDate)
    {
        var fiscalStart = _ledger.GetFiscalYearStart(asOfDate);
        var spokesTb = _ledger.CalculateTrialBalance(fiscalStart, asOfDate, includeZeroAccounts: false);
        var spokesSignedByCode = spokesTb.Rows.ToDictionary(
            r => r.AccountCode,
            r => r.IsDebitNormal ? r.EndingBalance : -r.EndingBalance,
            StringComparer.OrdinalIgnoreCase);
        return (spokesTb, spokesSignedByCode);
    }

    public void RecalculatePreviewTotals(AccountingMigrationPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var (_, spokesSignedByCode) = BuildSpokesSignedBalanceMap(preview.AsOfDate);

        // Re-sync SpokesSignedBalance when TargetGlCode is changed by user in UI
        foreach (var row in preview.GlMappings)
        {
            if (!row.Include) row.Action = MigrationGlAction.Ignore;
            if (row.Action == MigrationGlAction.MapToExisting && !string.IsNullOrWhiteSpace(row.TargetGlCode))
            {
                spokesSignedByCode.TryGetValue(row.TargetGlCode, out var sBal);
                row.SpokesSignedBalance = Math.Round(sBal, 2);
            }
            else
            {
                row.SpokesSignedBalance = 0m;
            }
        }

        preview.BlockingErrors.RemoveAll(e =>
            e.Contains("Trial Balance", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("unmapped", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("employee", StringComparison.OrdinalIgnoreCase));

        var activeGl = preview.GlMappings.Where(g => g.Include && g.Action != MigrationGlAction.Ignore).ToList();
        preview.HasExternalTrialBalanceBalances = activeGl.Any(g => g.DebitAmount != 0m || g.CreditAmount != 0m);
        preview.TotalExternalDebits = Math.Round(activeGl.Sum(g => g.DebitAmount), 2);
        preview.TotalExternalCredits = Math.Round(activeGl.Sum(g => g.CreditAmount), 2);

        // Group Delta lines by resolved TargetGlCode so merging multiple external accounts against one Spokes account compares combined totals cleanly
        var netByTarget = ComputeTargetNetDeltas(preview, spokesSignedByCode);
        preview.TotalDeltaDebits = Math.Round(netByTarget.Values.Where(v => v > 0m).Sum(), 2);
        preview.TotalDeltaCredits = Math.Round(netByTarget.Values.Where(v => v < 0m).Sum(Math.Abs), 2);

        // Destructive-delta safety guard: detect when a Delta cutover would wipe out >=80% of existing non-zero Spokes GL accounts
        preview.Warnings.RemoveAll(w => w.StartsWith("Destructive Delta Warning:", StringComparison.OrdinalIgnoreCase));
        if (preview.Mode == AccountingMigrationMode.ExistingWorkspaceDelta &&
            preview.HasExternalTrialBalanceBalances &&
            spokesSignedByCode.Count > 0)
        {
            var nonZeroSpokes = spokesSignedByCode.Where(kv => Math.Abs(kv.Value) > 0.01m).ToList();
            if (nonZeroSpokes.Count >= 2)
            {
                int zeroedOut = nonZeroSpokes.Count(kv =>
                    Math.Abs(kv.Value + netByTarget.GetValueOrDefault(kv.Key)) <= 0.01m);
                if ((decimal)zeroedOut / nonZeroSpokes.Count >= 0.80m)
                {
                    preview.RequiresDestructiveDeltaConfirmation = true;
                    preview.Warnings.Add($"Destructive Delta Warning: This cutover would zero out {zeroedOut} of {nonZeroSpokes.Count} existing non-zero Spokes GL accounts. Verify that your external Trial Balance is complete before confirming.");
                }
                else
                {
                    preview.RequiresDestructiveDeltaConfirmation = false;
                }
            }
            else
            {
                preview.RequiresDestructiveDeltaConfirmation = false;
            }
        }
        else
        {
            preview.RequiresDestructiveDeltaConfirmation = false;
        }

        var unmappedMissingCode = activeGl
            .Where(g => g.Action == MigrationGlAction.MapToExisting && string.IsNullOrWhiteSpace(g.TargetGlCode))
            .ToList();
        if (unmappedMissingCode.Count > 0)
        {
            preview.BlockingErrors.Add($"{unmappedMissingCode.Count} GL row(s) are set to 'Map to Existing' without a target Spokes GL account.");
        }

        var unmappedEmployees = preview.PayrollYtds
            .Where(p => p.Include && p.Action == MigrationEmployeeAction.LinkExisting && string.IsNullOrWhiteSpace(p.MatchedEmployeeId))
            .ToList();
        if (unmappedEmployees.Count > 0)
        {
            preview.BlockingErrors.Add($"{unmappedEmployees.Count} included Employee row(s) set to LinkExisting are not linked to a Spokes employee.");
        }

        if (activeGl.Count > 0 && Math.Abs(preview.TotalDeltaDebits - preview.TotalDeltaCredits) > 0.01m)
        {
            preview.BlockingErrors.Add(
                $"Trial Balance / Delta Journal Entry is out of balance: Debits ({preview.TotalDeltaDebits:C2}) != Credits ({preview.TotalDeltaCredits:C2}).");
        }

        // Project residual 3900 clearing balance after TB control lines + subledger open invoices/bills
        var includedAr = preview.OpenInvoices.Where(i => i.Include).ToList();
        var includedAp = preview.OpenBills.Where(b => b.Include).ToList();
        preview.ExpectedOpenArTotal = Math.Round(includedAr.Sum(i => i.OpenAmount), 2);
        preview.ExpectedOpenApTotal = Math.Round(includedAp.Sum(b => b.OpenAmount), 2);

        var arCode = _ledger.ResolveAccountCodeByRole(GlSystemRole.AccountsReceivable, "1200");
        var apCode = _ledger.ResolveAccountCodeByRole(GlSystemRole.AccountsPayable, "2100");
        var ob3900Code = _ledger.ResolveAccountCodeByRole(GlSystemRole.OpeningBalanceEquity, "3900");

        netByTarget.TryGetValue(arCode, out var tbArSigned);
        netByTarget.TryGetValue(apCode, out var tbApSigned); // typically negative (Credit)
        netByTarget.TryGetValue(ob3900Code, out var tb3900Signed);

        decimal clearingSigned = tb3900Signed;
        if (includedAr.Count > 0)
        {
            // TB AR line routes to 3900 Debit (+), while Open Invoices post Cr 3900 (-)
            clearingSigned += tbArSigned - preview.ExpectedOpenArTotal;
        }
        if (includedAp.Count > 0)
        {
            // TB AP line routes to 3900 Credit (tbApSigned is negative), while Open Bills post Dr 3900 (+)
            clearingSigned += tbApSigned + preview.ExpectedOpenApTotal;
        }

        preview.ProjectedClearing3900Residual = Math.Round(clearingSigned, 2);
        if (preview.GlMappings.Count == 0 && includedAr.Count == 0 && includedAp.Count == 0 &&
            preview.Vendors.Count == 0 && preview.Customers.Count == 0 &&
            preview.Projects.Count == 0 && preview.PayrollYtds.Count == 0 &&
            preview.InformationalNotices.Count == 0)
        {
            preview.BlockingErrors.Add("No migration records parsed to import.");
        }
    }

    public AccountingMigrationResult ExecuteMigration(AccountingMigrationPreview preview, string userId, string userName, Employee? executingEmployee = null)
    {
        DomainActorGuard.RequireEffectiveAdmin(executingEmployee, "execute an accounting migration");
        ArgumentNullException.ThrowIfNull(preview);
        RecalculatePreviewTotals(preview);
        if (!preview.CanExecute)
        {
            throw new InvalidOperationException($"Migration cannot execute until blocking errors are resolved: {string.Join("; ", preview.BlockingErrors)}");
        }

        var batch = new AccountingMigrationBatch
        {
            BatchNumber = _accounting.MigrationBatches?.GenerateBatchNumber() ?? _sequences.GenerateNumber("AccountingMigrationBatch", "MIG"),
            ExecutedAtUtc = DateTime.UtcNow,
            ExecutedByUserId = userId ?? string.Empty,
            ExecutedByUserName = string.IsNullOrWhiteSpace(userName) ? "System" : userName,
            Mode = preview.Mode,
            DetectedFormat = preview.DetectedFormat,
            AsOfDate = preview.AsOfDate.Date,
            SourceSummary = preview.SourceSummary,
            Status = AccountingMigrationBatchStatus.Applied
        };

        var result = new AccountingMigrationResult { Batch = batch };
        var actor = batch.ExecutedByUserName;

        // 0. Ensure 3900 OpeningBalanceEquity exists
        var obAccount = _templates != null
            ? _templates.EnsureOpeningBalanceEquitySeeded()
            : JurisdictionTemplateService.EnsureOpeningBalanceEquitySeeded(_accounting.GlAccounts);

        // 1. Create requested new GL accounts
        if (_accounting.GlAccounts != null)
        {
            foreach (var row in preview.GlMappings.Where(g => g.Include && g.Action == MigrationGlAction.CreateAccount && !g.IsSystemGeneratedSpokesOnly))
            {
                var targetNewCode = row.SourceCode.Trim();
                var existing = _accounting.GlAccounts.GetByCode(targetNewCode);
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(existing.SystemRole))
                    {
                        // Preserve the Spokes system account untouched by assigning a non-colliding suffix code for the imported account
                        var suffix = 1;
                        var candidateCode = $"{targetNewCode}-S";
                        while (_accounting.GlAccounts.GetByCode(candidateCode) != null)
                        {
                            candidateCode = $"{targetNewCode}-S{++suffix}";
                        }
                        targetNewCode = candidateCode;
                        row.SourceCode = targetNewCode;
                    }
                    else
                    {
                        row.TargetGlCode = existing.Code;
                        row.Action = MigrationGlAction.MapToExisting;
                        continue;
                    }
                }

                var subType = !string.IsNullOrWhiteSpace(row.ExternalSubType)
                    ? row.ExternalSubType
                    : InferSubType(targetNewCode, row.SourceType);

                var newAcct = new GlAccount
                {
                    Code = targetNewCode,
                    Name = row.SourceName.Trim(),
                    NameFr = row.SourceName.Trim(),
                    Type = string.IsNullOrWhiteSpace(row.SourceType) ? GlAccountType.Expense : row.SourceType,
                    SubType = subType,
                    StandardTaxCode = row.StandardTaxCode.Trim(),
                    IsActive = true,
                    IsSystemAccount = false
                };
                _accounting.GlAccounts.Save(newAcct);
                batch.CreatedGlAccountIds.Add(newAcct.Id);
                result.GlAccountsCreated++;
                row.TargetGlCode = newAcct.Code;
            }
        }

        // 2. Unified Address Book (Db.Suppliers) for both Vendors AND Customers (deduplicated)
        var addressBookByNormName = new Dictionary<string, (string Id, string Name, Supplier Entity)>(StringComparer.OrdinalIgnoreCase);
        if (_accounting.Suppliers != null)
        {
            void UpsertPartyIntoAddressBook(PartyMigrationRow party, bool countAsCustomer)
            {
                if (!party.Include || party.Action == MigrationPartyAction.Skip) return;
                var normName = PartyMatchingHelper.NormalizeCompanyName(party.ContactInfo.BusinessName);

                Supplier? existing = null;
                if (!string.IsNullOrEmpty(normName) && addressBookByNormName.TryGetValue(normName, out var alreadyInBatch))
                {
                    existing = alreadyInBatch.Entity;
                }
                else if (party.Action == MigrationPartyAction.LinkExisting && !string.IsNullOrWhiteSpace(party.MatchedEntityId))
                {
                    existing = _accounting.Suppliers.GetById(party.MatchedEntityId);
                }

                if (existing != null)
                {
                    MergeMissingContactInfo(existing.ContactInfo, party.ContactInfo);
                    EnsurePrimaryContactPerson(existing, party.ContactInfo);
                    _accounting.Suppliers.Save(existing);

                    if (countAsCustomer)
                    {
                        batch.MatchedClientsCount++;
                        result.ClientsMatched++;
                    }
                    else if (!addressBookByNormName.ContainsKey(normName))
                    {
                        batch.MatchedSuppliersCount++;
                        result.SuppliersMatched++;
                    }

                    if (!string.IsNullOrEmpty(normName))
                        addressBookByNormName[normName] = (existing.Id, existing.Name, existing);
                    return;
                }

                var created = new Supplier
                {
                    ContactInfo = CloneContact(party.ContactInfo)
                };
                EnsurePrimaryContactPerson(created, party.ContactInfo);
                _accounting.Suppliers.Save(created);
                batch.CreatedSupplierIds.Add(created.Id);
                if (countAsCustomer) result.ClientsCreated++;
                else result.SuppliersCreated++;

                if (!string.IsNullOrEmpty(normName))
                    addressBookByNormName[normName] = (created.Id, created.Name, created);
            }

            foreach (var v in preview.Vendors) UpsertPartyIntoAddressBook(v, countAsCustomer: false);
            foreach (var c in preview.Customers) UpsertPartyIntoAddressBook(c, countAsCustomer: true);
        }

        // Convert Dictionary<(string, string, Supplier)> to (string, string) view for subledger vendor lookup
        var vendorIdByNormName = addressBookByNormName.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value.Id, kvp.Value.Name),
            StringComparer.OrdinalIgnoreCase);

        // 3. Create / Link Real Projects (Db.Projects) linked to Address Book Clients
        var projectIdByNormCustomer = new Dictionary<string, (string Id, string Name)>(StringComparer.OrdinalIgnoreCase);
        if (_projects != null)
        {
            foreach (var pRow in preview.Projects)
            {
                if (!pRow.Include || pRow.Action == MigrationProjectAction.Skip) continue;

                if (pRow.Action == MigrationProjectAction.LinkExisting && !string.IsNullOrWhiteSpace(pRow.MatchedExistingProjectId))
                {
                    var existingProj = _projects.Projects.GetById(pRow.MatchedExistingProjectId);
                    if (existingProj != null)
                    {
                        var normClient = PartyMatchingHelper.NormalizeCompanyName(existingProj.Client?.BusinessName);
                        if (!string.IsNullOrEmpty(normClient) && !projectIdByNormCustomer.ContainsKey(normClient))
                        {
                            projectIdByNormCustomer[normClient] = (existingProj.Id, existingProj.Client?.BusinessName ?? existingProj.Name);
                        }
                        continue;
                    }
                }

                ClientInfo clientInfo = new();
                var clientNorm = PartyMatchingHelper.NormalizeCompanyName(pRow.MatchedClientName);
                if (!string.IsNullOrEmpty(clientNorm) && addressBookByNormName.TryGetValue(clientNorm, out var matchedSup))
                {
                    MergeMissingContactInfo(clientInfo, matchedSup.Entity.ContactInfo);
                }
                else if (!string.IsNullOrWhiteSpace(pRow.MatchedClientName))
                {
                    clientInfo.BusinessName = pRow.MatchedClientName;
                }

                var proj = new Project
                {
                    ProjectNumber = !string.IsNullOrWhiteSpace(pRow.ExtractedCode)
                        ? pRow.ExtractedCode
                        : _sequences.GenerateNumber("Project", "PRJ"),
                    Name = pRow.CleanName,
                    Description = $"Migrated from Sage 50 ({pRow.RawName}).",
                    Status = pRow.Action == MigrationProjectAction.CreateCompletedArchived
                        ? ProjectStatus.Completed
                        : ProjectStatus.InProduction,
                    IsActive = pRow.Action == MigrationProjectAction.CreateActive,
                    IsInternal = pRow.IsInternalBucket,
                    CreatedAt = pRow.StartDate ?? preview.AsOfDate,
                    Client = clientInfo
                };
                _projects.Projects.Save(proj);
                batch.CreatedProjectIds.Add(proj.Id);
                result.ProjectsCreated++;

                if (!string.IsNullOrEmpty(clientNorm) && !projectIdByNormCustomer.ContainsKey(clientNorm))
                {
                    projectIdByNormCustomer[clientNorm] = (proj.Id, proj.Client.BusinessName);
                }
            }
        }

        // 4. Open AR Invoices (cleared through 3900 Opening Balance Equity via LedgerService.SyncInvoice)
        var includedInvoices = preview.OpenInvoices.Where(i => i.Include && i.OpenAmount != 0m).ToList();
        if (_accounting.Invoices != null && includedInvoices.Count > 0)
        {
            foreach (var row in includedInvoices)
            {
                var projId = ResolveCustomerProjectId(row, projectIdByNormCustomer, batch, result, out var clientName);
                var invNumber = !string.IsNullOrWhiteSpace(row.DocumentNumber)
                    ? row.DocumentNumber.Trim()
                    : _accounting.Invoices.GenerateInvoiceNumber("MIG-INV");

                var inv = new Invoice
                {
                    ProjectId = projId,
                    InvoiceNumber = invNumber,
                    Date = row.IssueDate.Date,
                    DueDate = row.DueDate.Date,
                    Status = InvoiceStatus.Final,
                    Currency = string.IsNullOrWhiteSpace(row.Currency) ? preview.Currency : row.Currency,
                    ExchangeRate = 1.0m,
                    TaxRate = 0m,
                    Note = string.IsNullOrWhiteSpace(row.Notes)
                        ? $"Opening AR Balance migrated as of {preview.AsOfDate:yyyy-MM-dd} (Batch {batch.BatchNumber})."
                        : row.Notes,
                    IsOpeningBalance = true,
                    MigrationBatchId = batch.Id,
                    LineItems =
                    [
                        new InvoiceLine
                        {
                            Description = $"Opening Receivable Balance ({invNumber})",
                            Type = "General",
                            Quantity = 1m,
                            UnitPrice = Math.Round(row.OpenAmount, 2)
                        }
                    ]
                };

                DocumentHistoryHelper.RecordCreated(inv, actor, $"Opening balance invoice imported via migration batch {batch.BatchNumber}.");
                _accounting.Invoices.Save(inv);
                _ledger.SyncInvoice(inv, clientName);

                batch.CreatedInvoiceIds.Add(inv.Id);
                batch.TotalOpenArImported = Math.Round(batch.TotalOpenArImported + inv.GrandTotal, 2);
                result.OpenInvoicesCreated++;
            }
        }

        // 5. Open AP Bills (cleared through 3900 Opening Balance Equity via LedgerService.SyncBill)
        var includedBills = preview.OpenBills.Where(b => b.Include && b.OpenAmount != 0m).ToList();
        if (_accounting.Bills != null && includedBills.Count > 0)
        {
            foreach (var row in includedBills)
            {
                var (supplierId, supplierName) = ResolveVendorSupplier(row, vendorIdByNormName, batch, result);
                var isCreditNote = row.OpenAmount < 0m;
                var absAmt = Math.Round(Math.Abs(row.OpenAmount), 2);
                var billNum = !string.IsNullOrWhiteSpace(row.DocumentNumber)
                    ? row.DocumentNumber.Trim()
                    : _sequences.GenerateNumber("OpeningBill", "MIG-BILL");

                var bill = new Bill
                {
                    SupplierId = supplierId,
                    SupplierName = supplierName,
                    InvoiceNumber = billNum,
                    Date = row.IssueDate.Date,
                    DueDate = row.DueDate.Date,
                    Status = BillStatus.Approved,
                    Currency = string.IsNullOrWhiteSpace(row.Currency) ? preview.Currency : row.Currency,
                    ExchangeRate = 1.0m,
                    Amount = absAmt,
                    SubTotal = absAmt,
                    TaxAmount = 0m,
                    IsTaxExempt = true,
                    IsCreditNote = isCreditNote,
                    IsBusinessOverhead = true,
                    Description = string.IsNullOrWhiteSpace(row.Notes)
                        ? $"Opening AP Balance migrated as of {preview.AsOfDate:yyyy-MM-dd} (Batch {batch.BatchNumber})."
                        : row.Notes,
                    IsOpeningBalance = true,
                    MigrationBatchId = batch.Id
                };

                DocumentHistoryHelper.RecordCreated(bill, actor, $"Opening balance bill imported via migration batch {batch.BatchNumber}.");
                _accounting.Bills.Save(bill);
                _ledger.SyncBill(bill);

                batch.CreatedBillIds.Add(bill.Id);
                batch.TotalOpenApImported = Math.Round(batch.TotalOpenApImported + (isCreditNote ? -absAmt : absAmt), 2);
                result.OpenBillsCreated++;
            }
        }

        // 6. Post Balanced Opening Balance / Delta Journal Entry
        if (preview.GlMappings.Any(g => g.Action != MigrationGlAction.Ignore))
        {
            var fiscalStart = _ledger.GetFiscalYearStart(preview.AsOfDate);
            var spokesTb = _ledger.CalculateTrialBalance(fiscalStart, preview.AsOfDate, includeZeroAccounts: false);
            var spokesSignedByCode = spokesTb.Rows.ToDictionary(
                r => r.AccountCode,
                r => r.IsDebitNormal ? r.EndingBalance : -r.EndingBalance,
                StringComparer.OrdinalIgnoreCase);

            var netByTarget = ComputeTargetNetDeltas(preview, spokesSignedByCode);

            // Re-route AR and AP TB control lines through 3900 OpeningBalanceEquity when open subledger items are imported
            var arCode = _ledger.ResolveAccountCodeByRole(GlSystemRole.AccountsReceivable, "1200");
            var apCode = _ledger.ResolveAccountCodeByRole(GlSystemRole.AccountsPayable, "2100");
            if (includedInvoices.Count > 0 && netByTarget.TryGetValue(arCode, out var arDelta) && arDelta != 0m)
            {
                netByTarget.Remove(arCode);
                netByTarget[obAccount.Code] = netByTarget.GetValueOrDefault(obAccount.Code) + arDelta;
            }
            if (includedBills.Count > 0 && netByTarget.TryGetValue(apCode, out var apDelta) && apDelta != 0m)
            {
                netByTarget.Remove(apCode);
                netByTarget[obAccount.Code] = netByTarget.GetValueOrDefault(obAccount.Code) + apDelta;
            }

            var jeLines = new List<JournalLine>();
            var accountsByCode = (_accounting.GlAccounts?.GetAll() ?? []).ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in netByTarget.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var amt = Math.Round(kvp.Value, 2);
                if (amt == 0m) continue;

                accountsByCode.TryGetValue(kvp.Key, out var gl);
                jeLines.Add(new JournalLine
                {
                    GlAccountCode = kvp.Key,
                    GlAccountName = gl?.Name ?? kvp.Key,
                    Debit = amt > 0m ? amt : 0m,
                    Credit = amt < 0m ? Math.Abs(amt) : 0m,
                    Memo = $"Migration Cutover ({batch.BatchNumber}) as of {preview.AsOfDate:yyyy-MM-dd}"
                });
            }

            if (jeLines.Count >= 2)
            {
                var obEntry = new JournalEntry
                {
                    Date = preview.AsOfDate.Date,
                    Description = preview.Mode == AccountingMigrationMode.ExistingWorkspaceDelta
                        ? $"Accounting Migration Delta Entry ({batch.BatchNumber}) as of {preview.AsOfDate:yyyy-MM-dd}"
                        : $"Opening Balance Cutover Entry ({batch.BatchNumber}) as of {preview.AsOfDate:yyyy-MM-dd}",
                    Reference = batch.BatchNumber,
                    SourceType = JournalSourceType.OpeningBalance,
                    SourceId = batch.Id,
                    Currency = preview.Currency,
                    ExchangeRate = 1.0m,
                    CreatedBy = actor,
                    Lines = jeLines
                };

                var savedEntry = _ledger.SaveManualJournalEntry(obEntry);
                batch.CreatedJournalEntryIds.Add(savedEntry.Id);
                batch.OpeningTotalDebits = savedEntry.TotalDebits;
                batch.OpeningTotalCredits = savedEntry.TotalCredits;
                result.OpeningJournalEntryNumber = savedEntry.EntryNumber;
            }
        }

        // 7. Create / Link Employees & Seed Mid-Year Payroll YTD (PayRunStatus.OpeningYtd -> drives annual caps & T4/RL-1 without double-posting GL)
        if (_hr?.Employees != null)
        {
            foreach (var p in preview.PayrollYtds)
            {
                if (!p.Include || p.Action == MigrationEmployeeAction.Skip) continue;

                Employee? emp = null;
                if (p.Action == MigrationEmployeeAction.LinkExisting && !string.IsNullOrWhiteSpace(p.MatchedEmployeeId))
                {
                    emp = _hr.Employees.GetById(p.MatchedEmployeeId);
                    if (emp != null)
                    {
                        bool updated = false;
                        if (string.IsNullOrWhiteSpace(emp.SocialInsuranceNumber) && !string.IsNullOrWhiteSpace(p.SocialInsuranceNumber))
                        {
                            emp.SocialInsuranceNumber = p.SocialInsuranceNumber;
                            updated = true;
                        }
                        if (emp.DateOfBirth == null && p.BirthDate.HasValue)
                        {
                            emp.DateOfBirth = p.BirthDate;
                            updated = true;
                        }
                        if (string.IsNullOrWhiteSpace(emp.PhoneNumber) && !string.IsNullOrWhiteSpace(p.PhoneNumber))
                        {
                            emp.PhoneNumber = p.PhoneNumber;
                            updated = true;
                        }
                        if (string.IsNullOrWhiteSpace(emp.Address))
                        {
                            var addr = BuildFormattedEmployeeAddress(p);
                            if (!string.IsNullOrWhiteSpace(addr))
                            {
                                emp.Address = addr;
                                updated = true;
                            }
                        }
                        if (updated) _hr.Employees.Save(emp);
                        result.EmployeesLinked++;
                    }
                }
                else if (p.Action is MigrationEmployeeAction.CreateActive or MigrationEmployeeAction.CreateInactiveFormer)
                {
                    var payFreq = p.PayPeriodsPerYear switch
                    {
                        52 => PayFrequencyType.Weekly,
                        24 => PayFrequencyType.SemiMonthly,
                        12 => PayFrequencyType.Monthly,
                        _ => PayFrequencyType.BiWeekly
                    };

                    var fullAddr = BuildFormattedEmployeeAddress(p);
                    emp = new Employee
                    {
                        FirstName = !string.IsNullOrWhiteSpace(p.FirstName) ? p.FirstName : p.SourceEmployeeName,
                        LastName = p.LastName,
                        Email = p.SourceEmail ?? string.Empty,
                        PhoneNumber = p.PhoneNumber,
                        Address = fullAddr,
                        SocialInsuranceNumber = p.SocialInsuranceNumber,
                        DateOfBirth = p.BirthDate,
                        DateOfJoining = p.HireDate ?? DateOnly.FromDateTime(preview.AsOfDate),
                        IsActive = p.Action == MigrationEmployeeAction.CreateActive,
                        EmploymentEndDate = p.Action == MigrationEmployeeAction.CreateInactiveFormer ? DateOnly.FromDateTime(preview.AsOfDate) : null,
                        TeamId = _hr.Teams?.GetAll().FirstOrDefault()?.Id ?? string.Empty,
                        Payroll = new EmployeePayrollProfile
                        {
                            PayFrequency = payFreq
                        }
                    };
                    _hr.Employees.Save(emp);
                    batch.CreatedEmployeeIds.Add(emp.Id);
                    p.MatchedEmployeeId = emp.Id;
                    p.MatchedEmployeeName = emp.FullName;
                    result.EmployeesCreated++;
                }
            }
        }

        var includedPayroll = preview.PayrollYtds.Where(p => p.Include && p.Action != MigrationEmployeeAction.Skip && !string.IsNullOrWhiteSpace(p.MatchedEmployeeId) && p.GrossPayYtd > 0m).ToList();
        if (_hr?.PayRuns != null && includedPayroll.Count > 0)
        {
            var openingRun = new PayRun
            {
                RunNumber = _hr.PayRuns.GenerateRunNumber("MIG-YTD"),
                PeriodStart = new DateTime(preview.AsOfDate.Year, 1, 1),
                PeriodEnd = preview.AsOfDate.Date,
                PayDate = preview.AsOfDate.Date,
                TaxYear = preview.AsOfDate.Year,
                JurisdictionId = _templates?.GetActiveTemplate()?.JurisdictionId ?? "CA-QC",
                Currency = preview.Currency,
                Status = PayRunStatus.OpeningYtd,
                MigrationBatchId = batch.Id,
                Notes = $"Pre-cutover Payroll YTD seed ({batch.BatchNumber}) through {preview.AsOfDate:yyyy-MM-dd}. Excluded from GL sync to avoid double-counting Trial Balance.",
                FinalizedAt = DateTime.UtcNow
            };

            foreach (var p in includedPayroll)
            {
                var emp = _hr.Employees.GetById(p.MatchedEmployeeId!);
                if (emp == null) continue;

                var contribLines = BuildOpeningContributionLines(p);
                var taxLines = BuildOpeningTaxLines(p);

                openingRun.Stubs.Add(new PayStub
                {
                    EmployeeId = emp.Id,
                    EmployeeName = emp.FullName,
                    JobTitle = emp.Position,
                    RegularEarnings = Math.Round(p.GrossPayYtd, 2),
                    VacationAccruedDollars = Math.Round(p.VacationAccruedOrPaidYtd, 2),
                    StatutoryContributions = contribLines,
                    IncomeTaxLines = taxLines,
                    YtdGrossPay = Math.Round(p.GrossPayYtd, 2),
                    YtdFederalTax = Math.Round(p.FederalTaxYtd, 2),
                    YtdRegionalTax = Math.Round(p.RegionalTaxYtd, 2)
                });

                batch.MatchedEmployeesCount++;
                result.PayrollYtdStubsCreated++;
            }

            if (openingRun.Stubs.Count > 0)
            {
                DocumentHistoryHelper.RecordCreated(openingRun, actor, $"Opening YTD Payroll seed created by migration batch {batch.BatchNumber}.");
                _hr.PayRuns.Save(openingRun);
                batch.CreatedPayRunIds.Add(openingRun.Id);
            }
        }

        // Capture final 3900 balance check after all migration entries have been posted
        var postFiscalStart = _ledger.GetFiscalYearStart(preview.AsOfDate);
        var postTb = _ledger.CalculateTrialBalance(postFiscalStart, preview.AsOfDate, includeZeroAccounts: false);
        var post3900 = postTb.Rows.FirstOrDefault(r => string.Equals(r.AccountCode, obAccount.Code, StringComparison.OrdinalIgnoreCase));
        batch.ResidualClearingBalance3900 = post3900 != null ? Math.Round(post3900.EndingBalance, 2) : 0m;

        _accounting.MigrationBatches?.Save(batch);
        _logger?.LogInformation("Accounting migration batch {BatchNumber} executed by {User}: JE={JE}, AR={AR}, AP={AP}, YTD={YTD}",
            batch.BatchNumber, actor, result.OpeningJournalEntryNumber, result.OpenInvoicesCreated, result.OpenBillsCreated, result.PayrollYtdStubsCreated);

        return result;
    }

    public void RollbackMigrationBatch(string batchId, string userName, Employee? executingEmployee = null)
    {
        DomainActorGuard.RequireEffectiveAdmin(executingEmployee, "roll back an accounting migration batch");
        if (_accounting.MigrationBatches == null) throw new InvalidOperationException("MigrationBatches repository is not configured.");
        var batch = _accounting.MigrationBatches.GetById(batchId)
            ?? throw new KeyNotFoundException($"Migration batch '{batchId}' was not found.");

        if (string.Equals(batch.Status, AccountingMigrationBatchStatus.RolledBack, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Migration batch '{batch.BatchNumber}' has already been rolled back.");

        // Safety guard: refuse rollback if payments have been recorded against imported Invoices or Bills after cutover
        if (_accounting.Invoices != null)
        {
            foreach (var invId in batch.CreatedInvoiceIds)
            {
                var inv = _accounting.Invoices.GetById(invId);
                if (inv != null && (inv.Payments.Count > 0 || inv.AmountPaid > 0m))
                {
                    throw new InvalidOperationException(
                        $"Cannot roll back batch '{batch.BatchNumber}': imported invoice '{inv.InvoiceNumber}' already has recorded payment(s). Delete or reverse the payments first.");
                }
            }
        }

        if (_accounting.Bills != null)
        {
            foreach (var billId in batch.CreatedBillIds)
            {
                var bill = _accounting.Bills.GetById(billId);
                if (bill != null && (bill.Payments.Count > 0 || bill.AmountPaid > 0m))
                {
                    throw new InvalidOperationException(
                        $"Cannot roll back batch '{batch.BatchNumber}': imported bill '{bill.InvoiceNumber}' already has recorded payment(s). Reverse the payments first.");
                }
            }
        }

        // Remove GL entries and documents in safe dependency order
        foreach (var invId in batch.CreatedInvoiceIds)
        {
            _ledger.RemoveEntriesForSource(JournalSourceType.Invoice, invId);
            _accounting.Invoices?.Delete(invId);
        }

        foreach (var billId in batch.CreatedBillIds)
        {
            _ledger.RemoveEntriesForSource(JournalSourceType.Bill, billId);
            _accounting.Bills?.Delete(billId);
        }

        foreach (var jeId in batch.CreatedJournalEntryIds)
        {
            _accounting.JournalEntries?.Delete(jeId);
        }

        foreach (var runId in batch.CreatedPayRunIds)
        {
            _hr?.PayRuns?.Delete(runId);
        }

        foreach (var empId in batch.CreatedEmployeeIds)
        {
            _hr?.Employees?.Delete(empId);
        }

        foreach (var projId in batch.CreatedProjectIds)
        {
            _projects?.Projects?.Delete(projId);
        }

        foreach (var supId in batch.CreatedSupplierIds)
        {
            _accounting.Suppliers?.Delete(supId);
        }

        foreach (var glId in batch.CreatedGlAccountIds)
        {
            _accounting.GlAccounts?.Delete(glId);
        }

        batch.Status = AccountingMigrationBatchStatus.RolledBack;
        batch.RolledBackAtUtc = DateTime.UtcNow;
        batch.RolledBackByUserName = string.IsNullOrWhiteSpace(userName) ? "System" : userName;
        _accounting.MigrationBatches.Save(batch);
    }

    public List<AccountingMigrationBatch> GetMigrationHistory() =>
        _accounting.MigrationBatches?.GetRecent() ?? [];

    private static Dictionary<string, decimal> ComputeTargetNetDeltas(
        AccountingMigrationPreview preview,
        Dictionary<string, decimal> spokesSignedByCode)
    {
        var activeGl = preview.GlMappings.Where(g => g.Include && g.Action != MigrationGlAction.Ignore).ToList();
        if (!activeGl.Any(g => g.DebitAmount != 0m || g.CreditAmount != 0m))
        {
            // Master Data Only (CoA with $0 balances): never generate delta reversals that would wipe existing Spokes GL balances.
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        var externalByTarget = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in activeGl)
        {
            var code = row.Action == MigrationGlAction.CreateAccount ? row.SourceCode : row.TargetGlCode;
            if (string.IsNullOrWhiteSpace(code)) continue;
            externalByTarget[code] = Math.Round(externalByTarget.GetValueOrDefault(code) + row.ExternalSignedBalance, 2);
        }

        if (preview.Mode == AccountingMigrationMode.FreshCutover)
        {
            return externalByTarget;
        }

        var deltaByTarget = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var ignoredCodes = preview.GlMappings
            .Where(g => g.Action == MigrationGlAction.Ignore && !string.IsNullOrWhiteSpace(g.TargetGlCode))
            .Select(g => g.TargetGlCode!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var code in externalByTarget.Keys.Union(spokesSignedByCode.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (ignoredCodes.Contains(code)) continue;
            var ext = externalByTarget.GetValueOrDefault(code);
            var spk = spokesSignedByCode.GetValueOrDefault(code);
            var delta = Math.Round(ext - spk, 2);
            if (delta != 0m) deltaByTarget[code] = delta;
        }

        return deltaByTarget;
    }

    private string ResolveCustomerProjectId(
        OpenSubledgerMigrationRow row,
        Dictionary<string, (string Id, string Name)> projectIdByNormCustomer,
        AccountingMigrationBatch batch,
        AccountingMigrationResult result,
        out string clientName)
    {
        if (!string.IsNullOrWhiteSpace(row.ExistingProjectId))
        {
            var p = _projects?.Projects.GetById(row.ExistingProjectId);
            if (p != null)
            {
                clientName = p.Client?.BusinessName ?? p.Name;
                return p.Id;
            }
        }

        var norm = PartyMatchingHelper.NormalizeCompanyName(row.PartyName);
        if (!string.IsNullOrEmpty(norm) && projectIdByNormCustomer.TryGetValue(norm, out var cached))
        {
            clientName = cached.Name;
            return cached.Id;
        }

        var contact = new PartyContactInfo { BusinessName = row.PartyName.Trim() };
        var container = CreateClientContainerProject(contact);
        _projects?.Projects.Save(container);
        batch.CreatedProjectIds.Add(container.Id);
        result.ProjectsCreated++;

        if (!string.IsNullOrEmpty(norm))
        {
            projectIdByNormCustomer[norm] = (container.Id, container.Client.BusinessName);
        }

        clientName = container.Client.BusinessName;
        return container.Id;
    }

    private (string SupplierId, string SupplierName) ResolveVendorSupplier(
        OpenSubledgerMigrationRow row,
        Dictionary<string, (string Id, string Name)> vendorIdByNormName,
        AccountingMigrationBatch batch,
        AccountingMigrationResult result)
    {
        if (!string.IsNullOrWhiteSpace(row.MatchedPartyId))
        {
            var sup = _accounting.Suppliers?.GetById(row.MatchedPartyId);
            if (sup != null) return (sup.Id, sup.Name);
        }

        var norm = PartyMatchingHelper.NormalizeCompanyName(row.PartyName);
        if (!string.IsNullOrEmpty(norm) && vendorIdByNormName.TryGetValue(norm, out var cached))
        {
            return cached;
        }

        var created = new Supplier
        {
            ContactInfo = new PartyContactInfo { BusinessName = row.PartyName.Trim() }
        };
        _accounting.Suppliers?.Save(created);
        batch.CreatedSupplierIds.Add(created.Id);
        result.SuppliersCreated++;

        if (!string.IsNullOrEmpty(norm))
        {
            vendorIdByNormName[norm] = (created.Id, created.Name);
        }

        return (created.Id, created.Name);
    }

    private Project CreateClientContainerProject(PartyContactInfo info)
    {
        var client = new ClientInfo();
        MergeMissingContactInfo(client, info);

        return new Project
        {
            ProjectNumber = _sequences.GenerateNumber("Project", "MIG"),
            Name = $"{info.BusinessName} (Migrated Client)",
            Description = $"Container project created by accounting migration for {info.BusinessName}.",
            Status = ProjectStatus.WaitingForPayment,
            Client = client,
            IsActive = true
        };
    }

    private static List<PayStubContributionLine> BuildOpeningContributionLines(PayrollYtdMigrationRow row)
    {
        var lines = new List<PayStubContributionLine>();
        var gross = Math.Round(row.GrossPayYtd, 2);

        void AddLine(string code, string name, string authority, string box, decimal ee, decimal er)
        {
            if (ee <= 0m && er <= 0m) return;
            lines.Add(new PayStubContributionLine
            {
                Code = code,
                Name = name,
                Authority = authority,
                SlipBoxCode = box,
                InsurableEarningsThisPeriod = gross,
                EmployeeAmount = Math.Round(ee, 2),
                EmployerAmount = Math.Round(er, 2)
            });
        }

        AddLine("QPP", "QPP / CPP / RRQ (Opening YTD)", "Revenu Québec / CRA", "RL-1 Box B.A / T4 Box 17", row.QppCppEmployeeYtd, row.QppCppEmployerYtd);
        AddLine("QPP2", "QPP2 / CPP2 / RRQ2 (Opening YTD)", "Revenu Québec / CRA", "RL-1 Box B.B / T4 Box 17A", row.Qpp2EmployeeYtd, row.Qpp2EmployerYtd);
        AddLine("EI_QC", "EI / AE (Opening YTD)", "CRA", "T4 Box 18 / RL-1 Box C", row.EiEmployeeYtd, row.EiEmployerYtd);
        AddLine("QPIP", "QPIP / RQAP (Opening YTD)", "Revenu Québec", "RL-1 Box H / T4 Box 55", row.QpipEmployeeYtd, row.QpipEmployerYtd);

        foreach (var kvp in row.ContributionsByCode)
        {
            if (lines.Any(l => string.Equals(l.Code, kvp.Key, StringComparison.OrdinalIgnoreCase))) continue;
            AddLine(kvp.Key, $"{kvp.Key} (Opening YTD)", string.Empty, string.Empty, kvp.Value.Employee, kvp.Value.Employer);
        }

        return lines;
    }

    private static List<PayStubTaxLine> BuildOpeningTaxLines(PayrollYtdMigrationRow row)
    {
        var lines = new List<PayStubTaxLine>();
        var gross = Math.Round(row.GrossPayYtd, 2);
        if (row.FederalTaxYtd > 0m || row.RegionalTaxYtd > 0m)
        {
            lines.Add(new PayStubTaxLine
            {
                Code = "FED_TAX",
                Name = "Federal Income Tax (Opening YTD)",
                Authority = "CRA",
                SlipBoxCode = "T4 Box 22",
                TaxableIncomeThisPeriod = gross,
                WithheldAmount = Math.Round(row.FederalTaxYtd, 2)
            });
        }
        if (row.RegionalTaxYtd > 0m)
        {
            lines.Add(new PayStubTaxLine
            {
                Code = "PROV_TAX",
                Name = "Provincial / State Income Tax (Opening YTD)",
                Authority = "Revenu Québec",
                SlipBoxCode = "RL-1 Box E",
                TaxableIncomeThisPeriod = gross,
                WithheldAmount = Math.Round(row.RegionalTaxYtd, 2)
            });
        }
        return lines;
    }

    private static string InferSubType(string code, string type) => type switch
    {
        GlAccountType.Asset => code.StartsWith("15") || code.StartsWith("16") || code.StartsWith("17") ? GlAccountSubType.FixedAsset : GlAccountSubType.PrepaidOrOtherAsset,
        GlAccountType.Liability => GlAccountSubType.AccountsPayable,
        GlAccountType.Equity => GlAccountSubType.Equity,
        GlAccountType.Revenue => GlAccountSubType.OperatingRevenue,
        _ => code.StartsWith("50") || code.StartsWith("51") || code.StartsWith("52") ? GlAccountSubType.DirectCost : GlAccountSubType.Overhead
    };

    private static PartyContactInfo CloneContact(PartyContactInfo src)
    {
        var dst = new PartyContactInfo();
        MergeMissingContactInfo(dst, src);
        return dst;
    }

    private static void MergeMissingContactInfo(PartyContactInfo dst, PartyContactInfo src)
    {
        if (string.IsNullOrWhiteSpace(dst.BusinessName)) dst.BusinessName = src.BusinessName;
        if (string.IsNullOrWhiteSpace(dst.ContactPersonName)) dst.ContactPersonName = src.ContactPersonName;
        if (string.IsNullOrWhiteSpace(dst.ContactPersonEmail)) dst.ContactPersonEmail = src.ContactPersonEmail;
        if (string.IsNullOrWhiteSpace(dst.ContactPersonPhone)) dst.ContactPersonPhone = src.ContactPersonPhone;
        if (string.IsNullOrWhiteSpace(dst.BusinessAddressNumber)) dst.BusinessAddressNumber = src.BusinessAddressNumber;
        if (string.IsNullOrWhiteSpace(dst.BusinessAddressStreet)) dst.BusinessAddressStreet = src.BusinessAddressStreet;
        if (string.IsNullOrWhiteSpace(dst.BusinessAddressCity)) dst.BusinessAddressCity = src.BusinessAddressCity;
        if (string.IsNullOrWhiteSpace(dst.BusinessAddressState)) dst.BusinessAddressState = src.BusinessAddressState;
        if (string.IsNullOrWhiteSpace(dst.BusinessAddressCountry)) dst.BusinessAddressCountry = src.BusinessAddressCountry;
        if (string.IsNullOrWhiteSpace(dst.BusinessAddressZip)) dst.BusinessAddressZip = src.BusinessAddressZip;
        if (string.IsNullOrWhiteSpace(dst.TaxId)) dst.TaxId = src.TaxId;
        if (string.IsNullOrWhiteSpace(dst.DefaultGlAccountCode)) dst.DefaultGlAccountCode = src.DefaultGlAccountCode;
    }

    private static void EnsurePrimaryContactPerson(Supplier supplier, PartyContactInfo src)
    {
        if (string.IsNullOrWhiteSpace(src.ContactPersonName) && string.IsNullOrWhiteSpace(src.ContactPersonEmail))
            return;

        supplier.Contacts ??= [];
        var alreadyExists = supplier.Contacts.Any(c =>
            (!string.IsNullOrWhiteSpace(src.ContactPersonName) && string.Equals(c.Name.Trim(), src.ContactPersonName.Trim(), StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(src.ContactPersonEmail) && string.Equals(c.Email.Trim(), src.ContactPersonEmail.Trim(), StringComparison.OrdinalIgnoreCase)));

        if (!alreadyExists)
        {
            supplier.Contacts.Add(new ContactPerson
            {
                Name = src.ContactPersonName?.Trim() ?? string.Empty,
                Email = src.ContactPersonEmail?.Trim() ?? string.Empty,
                Phone = src.ContactPersonPhone?.Trim() ?? string.Empty,
                IsPrimary = supplier.Contacts.Count == 0
            });
        }
    }

    private static string BuildFormattedEmployeeAddress(PayrollYtdMigrationRow p)
    {
        var parts = new[] { p.Street1, p.Street2, p.City, p.Province, p.PostalCode }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim());
        return string.Join(", ", parts);
    }
}
