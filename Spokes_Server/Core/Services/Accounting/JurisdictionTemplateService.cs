namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

public interface IJurisdictionTemplateService
{
    IReadOnlyList<JurisdictionTemplate> GetAvailableTemplates();
    JurisdictionTemplate GetActiveTemplate();
    JurisdictionTemplate? GetTemplateById(string jurisdictionId, int? taxYear = null);
    (bool IsValid, string? ErrorMessage, JurisdictionTemplate? Template) ValidateAndParseTemplate(string json);
    JurisdictionTemplate SaveCustomTemplate(string json, Employee? actor = null);
    JurisdictionTemplate SaveTemplate(JurisdictionTemplate template, bool activateImmediately = true, Employee? actor = null);
    void ActivateTemplate(string jurisdictionId, int? taxYear = null, bool syncChartOfAccounts = true, Employee? actor = null);
    int EnsureChartOfAccountsSeeded(JurisdictionTemplate? template = null, bool updateExistingSystemAccounts = false, Employee? actor = null);
    GlAccount EnsureOpeningBalanceEquitySeeded();
    GlAccount EnsureBadDebtsAndWriteOffsSeeded(JurisdictionTemplate? template = null);
    string ExportTemplateJson(JurisdictionTemplate? template = null);
    IReadOnlyList<(string Label, string Number)> GetCompanyTaxRegistrations(DocumentTemplate? template = null);
}

public class JurisdictionTemplateService : IJurisdictionTemplateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly GlAccountRepository _glAccounts;
    private readonly CompanyProfileRepository _companyProfileRepo;
    private readonly string _customTemplatesDir;
    private readonly ILogger<JurisdictionTemplateService>? _logger;
    private readonly object _syncLock = new();
    private List<JurisdictionTemplate>? _cachedTemplates;

    public JurisdictionTemplateService(
        GlAccountRepository glAccounts,
        CompanyProfileRepository companyProfileRepo,
        IConfiguration? config = null,
        ILogger<JurisdictionTemplateService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(glAccounts);
        ArgumentNullException.ThrowIfNull(companyProfileRepo);

        _glAccounts = glAccounts;
        _companyProfileRepo = companyProfileRepo;
        _logger = logger;

        var dataPath = config?["DataPath"] ?? "Data";
        _customTemplatesDir = Path.Combine(dataPath, "AccountingTemplates");

        InitializeActiveTemplateAndChartOfAccounts();
    }

    public IReadOnlyList<JurisdictionTemplate> GetAvailableTemplates()
    {
        lock (_syncLock)
        {
            if (_cachedTemplates == null || _cachedTemplates.Count == 0)
            {
                _cachedTemplates = LoadAllTemplatesInternal();
            }
            return _cachedTemplates.ToList();
        }
    }

    public JurisdictionTemplate GetActiveTemplate()
    {
        var profile = _companyProfileRepo.Get();
        var activeId = profile?.Accounting?.ActiveJurisdictionId;
        var activeYear = profile?.Accounting?.ActiveTaxYear;

        if (!string.IsNullOrWhiteSpace(activeId))
        {
            var match = GetTemplateById(activeId, activeYear) ?? GetTemplateById(activeId);
            if (match != null)
            {
                FinancialCalculationEngine.ActiveTemplate = match;
                return match;
            }
        }

        var fallback = GetTemplateById("CA-QC") ?? GetAvailableTemplates().FirstOrDefault() ?? BuildFallbackQuebecTemplate();
        FinancialCalculationEngine.ActiveTemplate = fallback;
        return fallback;
    }

    public JurisdictionTemplate? GetTemplateById(string jurisdictionId, int? taxYear = null)
    {
        if (string.IsNullOrWhiteSpace(jurisdictionId)) return null;
        var all = GetAvailableTemplates();

        if (taxYear.HasValue && taxYear.Value > 0)
        {
            var exact = all.FirstOrDefault(t =>
                string.Equals(t.JurisdictionId, jurisdictionId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                t.TaxYear == taxYear.Value);
            if (exact != null) return exact;
        }

        return all
            .Where(t => string.Equals(t.JurisdictionId, jurisdictionId.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.TaxYear)
            .FirstOrDefault();
    }

    public (bool IsValid, string? ErrorMessage, JurisdictionTemplate? Template) ValidateAndParseTemplate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (false, "Template JSON is empty.", null);
        }

        try
        {
            var template = JsonSerializer.Deserialize<JurisdictionTemplate>(json, JsonOptions);
            if (template == null)
            {
                return (false, "Unable to deserialize JurisdictionTemplate JSON.", null);
            }

            if (string.IsNullOrWhiteSpace(template.JurisdictionId))
            {
                return (false, "JurisdictionId is required (e.g. 'CA-QC').", null);
            }

            if (string.IsNullOrWhiteSpace(template.Name))
            {
                return (false, "Template Name is required.", null);
            }

            if (template.TaxYear < 2000 || template.TaxYear > 2100)
            {
                return (false, "TaxYear must be between 2000 and 2100.", null);
            }

            if (template.ChartOfAccounts == null || template.ChartOfAccounts.Count == 0)
            {
                return (false, "ChartOfAccounts must contain at least one GL account.", null);
            }

            foreach (var acct in template.ChartOfAccounts)
            {
                if (string.IsNullOrWhiteSpace(acct.Code) || string.IsNullOrWhiteSpace(acct.Name))
                {
                    return (false, "Every ChartOfAccounts item must have a non-empty Code and Name.", null);
                }
            }

            return (true, null, template);
        }
        catch (Exception ex)
        {
            return (false, $"Invalid JSON format: {ex.Message}", null);
        }
    }

    private static void RequireTemplateMutationPermissions(Employee? actor, string action)
    {
        if (actor == null) return;
        if (actor.IsSystem)
        {
            throw new UnauthorizedAccessException($"System actors cannot {action}.");
        }

        DomainActorGuard.RequirePermission(actor, AppPermissions.Business.ManageSettings, action, allowSuspended: false);
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, action, allowSuspended: false);
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, action, allowSuspended: false);
    }

    public JurisdictionTemplate SaveCustomTemplate(string json, Employee? actor = null)
    {
        RequireTemplateMutationPermissions(actor, "save custom jurisdiction templates");
        var (isValid, error, template) = ValidateAndParseTemplate(json);
        if (!isValid || template == null)
        {
            throw new InvalidOperationException(error ?? "Invalid jurisdiction template.");
        }

        return SaveTemplate(template, activateImmediately: false, actor: actor);
    }

    public JurisdictionTemplate SaveTemplate(JurisdictionTemplate template, bool activateImmediately = true, Employee? actor = null)
    {
        RequireTemplateMutationPermissions(actor, "save jurisdiction templates");
        ArgumentNullException.ThrowIfNull(template);
        var json = JsonSerializer.Serialize(template, JsonOptions);
        var (isValid, error, validated) = ValidateAndParseTemplate(json);
        if (!isValid || validated == null)
        {
            throw new InvalidOperationException(error ?? "Invalid jurisdiction template.");
        }

        Directory.CreateDirectory(_customTemplatesDir);
        var safeId = string.Concat(validated.JurisdictionId.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
        var fileName = $"{safeId}-{validated.TaxYear}.spokes-jurisdiction.json";
        var filePath = Path.Combine(_customTemplatesDir, fileName);
        File.WriteAllText(filePath, json);

        lock (_syncLock)
        {
            _cachedTemplates = LoadAllTemplatesInternal();
        }

        if (activateImmediately)
        {
            ActivateTemplate(validated.JurisdictionId, validated.TaxYear, syncChartOfAccounts: true, actor: actor);
        }

        return validated;
    }

    private volatile bool _chartSeedVerified;

    public void ActivateTemplate(string jurisdictionId, int? taxYear = null, bool syncChartOfAccounts = true, Employee? actor = null)
    {
        RequireTemplateMutationPermissions(actor, "activate jurisdiction templates");
        _chartSeedVerified = false;
        var template = GetTemplateById(jurisdictionId, taxYear)
            ?? throw new KeyNotFoundException($"Jurisdiction template '{jurisdictionId}' was not found.");

        FinancialCalculationEngine.ActiveTemplate = template;

        var profile = _companyProfileRepo.Get();
        if (profile != null)
        {
            profile.Accounting ??= new();
            profile.Accounting.ActiveJurisdictionId = template.JurisdictionId;
            profile.Accounting.ActiveTaxYear = template.TaxYear;
            profile.Accounting.DefaultCurrency = template.DefaultCurrency;
            profile.Accounting.DefaultTaxRate = template.CombinedSalesTaxRate;
            profile.Accounting.KilometrageRate = template.MileageRules.PrimaryRatePerKm;
            profile.Accounting.SecondaryKilometrageRate = template.MileageRules.SecondaryRatePerKm;
            profile.Accounting.KilometrageTierThresholdKm = template.MileageRules.TierThresholdKm;
            _companyProfileRepo.Save(profile);
        }

        if (syncChartOfAccounts)
        {
            EnsureChartOfAccountsSeeded(template, updateExistingSystemAccounts: true, actor: actor);
        }
    }

    public int EnsureChartOfAccountsSeeded(JurisdictionTemplate? template = null, bool updateExistingSystemAccounts = false, Employee? actor = null)
    {
        if (actor != null)
        {
            if (actor.IsSystem)
            {
                throw new UnauthorizedAccessException("System actors cannot seed chart of accounts.");
            }
            DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "seed chart of accounts", allowSuspended: false);
        }
        if (template == null && !updateExistingSystemAccounts && _chartSeedVerified && _glAccounts.GetAll().Any())
        {
            return 0;
        }

        var active = template ?? GetActiveTemplate();
        var existingByCode = _glAccounts.GetAll()
            .Where(a => !string.IsNullOrWhiteSpace(a.Code))
            .GroupBy(a => a.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int modifiedCount = 0;

        foreach (var item in active.ChartOfAccounts)
        {
            if (string.IsNullOrWhiteSpace(item.Code)) continue;
            var code = item.Code.Trim();

            if (!existingByCode.TryGetValue(code, out var existing))
            {
                var newAccount = new GlAccount
                {
                    Id = $"gl-{code}",
                    Code = code,
                    Name = item.Name,
                    NameFr = !string.IsNullOrWhiteSpace(item.NameFr) ? item.NameFr : item.Name,
                    Type = item.Type,
                    SubType = item.SubType,
                    StandardTaxCode = item.StandardTaxCode,
                    SystemRole = item.SystemRole,
                    Currency = active.DefaultCurrency,
                    IsSystemAccount = !string.IsNullOrWhiteSpace(item.SystemRole),
                    IsActive = true
                };
                _glAccounts.Save(newAccount);
                existingByCode[code] = newAccount;
                modifiedCount++;
            }
            else if (updateExistingSystemAccounts)
            {
                bool changed = false;
                if (string.IsNullOrWhiteSpace(existing.StandardTaxCode) && !string.IsNullOrWhiteSpace(item.StandardTaxCode))
                {
                    existing.StandardTaxCode = item.StandardTaxCode;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(existing.SystemRole) && !string.IsNullOrWhiteSpace(item.SystemRole))
                {
                    existing.SystemRole = item.SystemRole;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(existing.NameFr) && !string.IsNullOrWhiteSpace(item.NameFr))
                {
                    existing.NameFr = item.NameFr;
                    changed = true;
                }
                if (changed)
                {
                    _glAccounts.Save(existing);
                    modifiedCount++;
                }
            }
        }

        if (template == null && !updateExistingSystemAccounts && active.ChartOfAccounts.Count > 0)
        {
            _chartSeedVerified = true;
        }

        return modifiedCount;
    }

    public string ExportTemplateJson(JurisdictionTemplate? template = null)
    {
        var target = template ?? GetActiveTemplate();
        return JsonSerializer.Serialize(target, JsonOptions);
    }

    public IReadOnlyList<(string Label, string Number)> GetCompanyTaxRegistrations(DocumentTemplate? template = null)
    {
        var profile = _companyProfileRepo.Get();
        var map = profile?.Accounting?.TaxRegistrationNumbersByCode;
        if (map == null || map.Count == 0)
            return [];

        var active = GetActiveTemplate();
        var results = new List<(string Label, string Number)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (active.SalesTaxes != null)
        {
            foreach (var tax in active.SalesTaxes)
            {
                if (string.IsNullOrWhiteSpace(tax.Code)) continue;
                if (map.TryGetValue(tax.Code, out var number) && !string.IsNullOrWhiteSpace(number))
                {
                    var label = TryGetTemplateTaxLabel(template, tax.Code)
                        ?? (!string.IsNullOrWhiteSpace(tax.RegistrationNumberLabel)
                            ? tax.RegistrationNumberLabel
                            : tax.Code);
                    results.Add((label.TrimEnd(':', ' ') + ":", number.Trim()));
                    seen.Add(tax.Code);
                }
            }
        }

        foreach (var kvp in map)
        {
            if (seen.Contains(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value)) continue;
            var label = TryGetTemplateTaxLabel(template, kvp.Key) ?? kvp.Key.Trim();
            results.Add((label.TrimEnd(':', ' ') + ":", kvp.Value.Trim()));
        }

        return results;
    }

    private static string? TryGetTemplateTaxLabel(DocumentTemplate? template, string code)
    {
        var labels = template?.TaxRegistrationLabelsByCode;
        if (labels == null || labels.Count == 0 || string.IsNullOrWhiteSpace(code))
            return null;

        if (labels.TryGetValue(code, out var exact) && !string.IsNullOrWhiteSpace(exact))
            return exact.Trim();

        foreach (var kvp in labels)
        {
            if (string.Equals(kvp.Key, code, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kvp.Value))
                return kvp.Value.Trim();
        }

        return null;
    }

    private void InitializeActiveTemplateAndChartOfAccounts()
    {
        try
        {
            var active = GetActiveTemplate();
            FinancialCalculationEngine.ActiveTemplate = active;
            if (_glAccounts.GetAll().Count == 0)
            {
                EnsureChartOfAccountsSeeded(active);
            }
            EnsureOpeningBalanceEquitySeeded();
            EnsurePettyCashAccountSeeded(active);
            EnsureBadDebtsAndWriteOffsSeeded(active);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to initialize default jurisdiction template on startup; using fallback.");
            var fallback = BuildFallbackQuebecTemplate();
            FinancialCalculationEngine.ActiveTemplate = fallback;
            if (_glAccounts.GetAll().Count == 0)
            {
                EnsureChartOfAccountsSeeded(fallback);
            }
            EnsureOpeningBalanceEquitySeeded();
            EnsurePettyCashAccountSeeded(fallback);
            EnsureBadDebtsAndWriteOffsSeeded(fallback);
        }
    }

    /// <summary>
    /// Ensures an OpeningBalanceEquity clearing account exists even for workspaces seeded before role 3900 was introduced.
    /// </summary>
    public GlAccount EnsureOpeningBalanceEquitySeeded() =>
        EnsureOpeningBalanceEquitySeeded(_glAccounts);

    internal static GlAccount EnsureOpeningBalanceEquitySeeded(GlAccountRepository? glAccounts)
    {
        var all = glAccounts?.GetAll() ?? [];
        var existingByRole = all.FirstOrDefault(a => string.Equals(a.SystemRole, GlSystemRole.OpeningBalanceEquity, StringComparison.OrdinalIgnoreCase));
        if (existingByRole != null) return existingByRole;

        var existing3900 = all.FirstOrDefault(a => string.Equals(a.Code, "3900", StringComparison.OrdinalIgnoreCase));
        if (existing3900 != null)
        {
            if (string.IsNullOrWhiteSpace(existing3900.SystemRole))
            {
                existing3900.SystemRole = GlSystemRole.OpeningBalanceEquity;
                glAccounts?.Save(existing3900);
            }
            return existing3900;
        }

        var account = new GlAccount
        {
            Code = "3900",
            Name = "Opening Balance Equity",
            NameFr = "Capital d'ouverture (Compensation)",
            Type = GlAccountType.Equity,
            SubType = GlAccountSubType.Equity,
            StandardTaxCode = "3600",
            SystemRole = GlSystemRole.OpeningBalanceEquity,
            IsSystemAccount = true,
            IsActive = true
        };
        glAccounts?.Save(account);
        return account;
    }

    /// <summary>
    /// Ensures a Cash on Hand / Petty Cash account (1010) exists even for workspaces seeded before PettyCash was introduced.
    /// </summary>
    public GlAccount EnsurePettyCashAccountSeeded(JurisdictionTemplate? template = null) =>
        EnsurePettyCashAccountSeeded(_glAccounts, template);

    internal static GlAccount EnsurePettyCashAccountSeeded(GlAccountRepository? glAccounts, JurisdictionTemplate? template = null)
    {
        var all = glAccounts?.GetAll() ?? [];
        var existingByRole = all.FirstOrDefault(a => string.Equals(a.SystemRole, GlSystemRole.PettyCash, StringComparison.OrdinalIgnoreCase));
        if (existingByRole != null) return existingByRole;

        var existing1010 = all.FirstOrDefault(a => string.Equals(a.Code, "1010", StringComparison.OrdinalIgnoreCase));
        if (existing1010 != null)
        {
            if (string.IsNullOrWhiteSpace(existing1010.SystemRole))
            {
                existing1010.SystemRole = GlSystemRole.PettyCash;
                glAccounts?.Save(existing1010);
            }
            return existing1010;
        }

        var tmplItem = template?.ChartOfAccounts?.FirstOrDefault(a =>
            string.Equals(a.SystemRole, GlSystemRole.PettyCash, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Code, "1010", StringComparison.OrdinalIgnoreCase));

        var account = new GlAccount
        {
            Id = $"gl-{tmplItem?.Code ?? "1010"}",
            Code = tmplItem?.Code ?? "1010",
            Name = tmplItem?.Name ?? "Cash on Hand / Petty Cash",
            NameFr = tmplItem?.NameFr ?? "Petite caisse / Encaisse",
            Type = GlAccountType.Asset,
            SubType = GlAccountSubType.Bank,
            StandardTaxCode = tmplItem?.StandardTaxCode ?? "1001",
            SystemRole = GlSystemRole.PettyCash,
            Currency = template?.DefaultCurrency ?? "CAD",
            IsSystemAccount = true,
            IsActive = true
        };
        glAccounts?.Save(account);
        return account;
    }

    /// <summary>
    /// Ensures a Bad Debts &amp; Balance Write-Offs account (5860) exists even for workspaces seeded before BadDebtsAndWriteOffs was introduced.
    /// </summary>
    public GlAccount EnsureBadDebtsAndWriteOffsSeeded(JurisdictionTemplate? template = null) =>
        EnsureBadDebtsAndWriteOffsSeeded(_glAccounts, template);

    internal static GlAccount EnsureBadDebtsAndWriteOffsSeeded(GlAccountRepository? glAccounts, JurisdictionTemplate? template = null)
    {
        var all = glAccounts?.GetAll() ?? [];
        var existingByRole = all.FirstOrDefault(a => string.Equals(a.SystemRole, GlSystemRole.BadDebtsAndWriteOffs, StringComparison.OrdinalIgnoreCase));
        if (existingByRole != null) return existingByRole;

        var existing5860 = all.FirstOrDefault(a => string.Equals(a.Code, "5860", StringComparison.OrdinalIgnoreCase));
        if (existing5860 != null)
        {
            if (string.IsNullOrWhiteSpace(existing5860.SystemRole))
            {
                existing5860.SystemRole = GlSystemRole.BadDebtsAndWriteOffs;
                glAccounts?.Save(existing5860);
            }
            return existing5860;
        }

        var tmplItem = template?.ChartOfAccounts?.FirstOrDefault(a =>
            string.Equals(a.SystemRole, GlSystemRole.BadDebtsAndWriteOffs, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Code, "5860", StringComparison.OrdinalIgnoreCase));

        var account = new GlAccount
        {
            Id = $"gl-{tmplItem?.Code ?? "5860"}",
            Code = tmplItem?.Code ?? "5860",
            Name = tmplItem?.Name ?? "Bad Debts & Balance Write-Offs",
            NameFr = tmplItem?.NameFr ?? "Créances irrécouvrables et radiations de soldes",
            Type = GlAccountType.Expense,
            SubType = GlAccountSubType.Overhead,
            StandardTaxCode = tmplItem?.StandardTaxCode ?? "8590",
            SystemRole = GlSystemRole.BadDebtsAndWriteOffs,
            Currency = template?.DefaultCurrency ?? "CAD",
            IsSystemAccount = true,
            IsActive = true
        };
        glAccounts?.Save(account);
        return account;
    }

    private List<JurisdictionTemplate> LoadAllTemplatesInternal()
    {
        var templatesByKey = new Dictionary<string, JurisdictionTemplate>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in GetCandidateTemplateDirectories())
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (var file in Directory.GetFiles(dir, "*.spokes-jurisdiction.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var (isValid, _, parsed) = ValidateAndParseTemplate(json);
                        if (isValid && parsed != null)
                        {
                            var key = $"{parsed.JurisdictionId}:{parsed.TaxYear}";
                            templatesByKey[key] = parsed;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Skipping invalid jurisdiction template file {File}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Unable to scan template directory {Directory}", dir);
            }
        }

        if (!templatesByKey.Keys.Any(k => k.StartsWith("CA-QC:", StringComparison.OrdinalIgnoreCase)))
        {
            var fallbackQc = BuildFallbackQuebecTemplate();
            templatesByKey[$"{fallbackQc.JurisdictionId}:{fallbackQc.TaxYear}"] = fallbackQc;
        }

        return templatesByKey.Values
            .OrderBy(t => t.JurisdictionId == "CA-QC" ? 0 : 1)
            .ThenBy(t => t.Name)
            .ThenByDescending(t => t.TaxYear)
            .ToList();
    }

    private IEnumerable<string> GetCandidateTemplateDirectories()
    {
        var dirs = new List<string>();

        var roots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var root in roots)
        {
            var curr = new DirectoryInfo(root);
            for (int depth = 0; depth < 6 && curr != null; depth++)
            {
                var candidate = Path.Combine(curr.FullName, "Spokes_Marketplace", "accounting-templates");
                if (!dirs.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    dirs.Add(candidate);
                }
                curr = curr.Parent;
            }
        }

        if (!string.IsNullOrWhiteSpace(_customTemplatesDir))
        {
            dirs.Add(_customTemplatesDir);
        }

        return dirs;
    }

    public static JurisdictionTemplate BuildFallbackQuebecTemplate()
    {
        return new JurisdictionTemplate
        {
            SchemaVersion = "1.0",
            JurisdictionId = "CA-QC",
            Name = "Canada — Québec",
            NameFr = "Canada — Québec",
            CountryCode = "CA",
            RegionCode = "QC",
            TaxYear = 2026,
            DefaultCurrency = "CAD",
            TaxMappingStandard = "GIFI",
            ChartOfAccounts =
            [
                new() { Code = "1010", Name = "Cash on Hand / Petty Cash", NameFr = "Petite caisse / Encaisse", Type = GlAccountType.Asset, SubType = GlAccountSubType.Bank, StandardTaxCode = "1001", SystemRole = GlSystemRole.PettyCash },
                new() { Code = "1020", Name = "Operating Bank Account (Chequing)", NameFr = "Compte bancaire courant (Opérations)", Type = GlAccountType.Asset, SubType = GlAccountSubType.Bank, StandardTaxCode = "1002", SystemRole = GlSystemRole.OperatingBank },
                new() { Code = "1030", Name = "Savings / Foreign Currency Bank Account", NameFr = "Compte bancaire épargne / devises", Type = GlAccountType.Asset, SubType = GlAccountSubType.Bank, StandardTaxCode = "1003" },
                new() { Code = "1200", Name = "Accounts Receivable (Clients)", NameFr = "Comptes clients à recevoir", Type = GlAccountType.Asset, SubType = GlAccountSubType.AccountsReceivable, StandardTaxCode = "1060", SystemRole = GlSystemRole.AccountsReceivable },
                new() { Code = "1300", Name = "Prepaid Expenses & Deposits", NameFr = "Frais payés d'avance et dépôts", Type = GlAccountType.Asset, SubType = GlAccountSubType.PrepaidOrOtherAsset, StandardTaxCode = "1484", SystemRole = GlSystemRole.PrepaidExpenses },
                new() { Code = "1500", Name = "Computer & Robotics Equipment", NameFr = "Équipement informatique et robotique", Type = GlAccountType.Asset, SubType = GlAccountSubType.FixedAsset, StandardTaxCode = "1774" },
                new() { Code = "1510", Name = "Accumulated Amortization — Equipment", NameFr = "Amortissement cumulé — Équipement", Type = GlAccountType.Asset, SubType = GlAccountSubType.FixedAsset, StandardTaxCode = "1775" },
                new() { Code = "2100", Name = "Accounts Payable (Suppliers)", NameFr = "Comptes fournisseurs à payer", Type = GlAccountType.Liability, SubType = GlAccountSubType.AccountsPayable, StandardTaxCode = "2621", SystemRole = GlSystemRole.AccountsPayable },
                new() { Code = "2120", Name = "Corporate Credit Card", NameFr = "Carte de crédit d'entreprise", Type = GlAccountType.Liability, SubType = GlAccountSubType.CreditCard, StandardTaxCode = "2620", SystemRole = GlSystemRole.CorporateCreditCard },
                new() { Code = "2140", Name = "Employee Expense Reimbursements Payable", NameFr = "Comptes de dépenses employés à payer", Type = GlAccountType.Liability, SubType = GlAccountSubType.AccountsPayable, StandardTaxCode = "2620", SystemRole = GlSystemRole.EmployeeReimbursementsPayable },
                new() { Code = "2150", Name = "Commissions Payable", NameFr = "Commissions à payer", Type = GlAccountType.Liability, SubType = GlAccountSubType.AccountsPayable, StandardTaxCode = "2624", SystemRole = GlSystemRole.CommissionsPayable },
                new() { Code = "2160", Name = "TPS / GST Collected on Sales", NameFr = "TPS perçue sur les ventes", Type = GlAccountType.Liability, SubType = GlAccountSubType.SalesTax, StandardTaxCode = "2680", SystemRole = GlSystemRole.FederalTaxCollected },
                new() { Code = "2165", Name = "TPS / GST Input Tax Credits (CTI / ITC)", NameFr = "TPS à recevoir (CTI sur achats)", Type = GlAccountType.Liability, SubType = GlAccountSubType.SalesTax, StandardTaxCode = "2680", SystemRole = GlSystemRole.FederalTaxRecoverable },
                new() { Code = "2170", Name = "TVQ / QST Collected on Sales", NameFr = "TVQ perçue sur les ventes", Type = GlAccountType.Liability, SubType = GlAccountSubType.SalesTax, StandardTaxCode = "2680", SystemRole = GlSystemRole.RegionalTaxCollected },
                new() { Code = "2175", Name = "TVQ / QST Input Tax Refunds (RTI / ITR)", NameFr = "TVQ à recevoir (RTI sur achats)", Type = GlAccountType.Liability, SubType = GlAccountSubType.SalesTax, StandardTaxCode = "2680", SystemRole = GlSystemRole.RegionalTaxRecoverable },
                new() { Code = "2180", Name = "Sales Tax Remittance Payable (TPS/TVQ Net)", NameFr = "Remise de taxes de vente à payer (TPS/TVQ nette)", Type = GlAccountType.Liability, SubType = GlAccountSubType.SalesTax, StandardTaxCode = "2680", SystemRole = GlSystemRole.SalesTaxRemittancePayable },
                new() { Code = "2190", Name = "Corporate Income Taxes Payable (T2 / CO-17)", NameFr = "Impôts sur le revenu des sociétés à payer (T2 / CO-17)", Type = GlAccountType.Liability, SubType = GlAccountSubType.OtherLiability, StandardTaxCode = "2680" },
                new() { Code = "2200", Name = "Net Wages & Salaries Payable", NameFr = "Salaires nets à payer", Type = GlAccountType.Liability, SubType = GlAccountSubType.PayrollLiability, StandardTaxCode = "2624", SystemRole = GlSystemRole.WagesPayable },
                new() { Code = "2210", Name = "Payroll Source Deductions Payable (DAS QC & CRA)", NameFr = "Déductions à la source à payer (DAS Revenu Québec et ARC)", Type = GlAccountType.Liability, SubType = GlAccountSubType.PayrollLiability, StandardTaxCode = "2627", SystemRole = GlSystemRole.PayrollDeductionsPayable },
                new() { Code = "2220", Name = "Vacation Pay Accrued Payable (4% / 6%)", NameFr = "Vacances accumulées à payer (4 % / 6 %)", Type = GlAccountType.Liability, SubType = GlAccountSubType.PayrollLiability, StandardTaxCode = "2624", SystemRole = GlSystemRole.VacationPayAccrued },
                new() { Code = "3000", Name = "Share Capital / Owner's Equity", NameFr = "Capital-actions / Avoir du propriétaire", Type = GlAccountType.Equity, SubType = GlAccountSubType.Equity, StandardTaxCode = "3500", SystemRole = GlSystemRole.OwnerEquity },
                new() { Code = "3600", Name = "Retained Earnings", NameFr = "Bénéfices non répartis", Type = GlAccountType.Equity, SubType = GlAccountSubType.Equity, StandardTaxCode = "3600", SystemRole = GlSystemRole.RetainedEarnings },
                new() { Code = "3900", Name = "Opening Balance Equity", NameFr = "Capital d'ouverture (Compensation)", Type = GlAccountType.Equity, SubType = GlAccountSubType.Equity, StandardTaxCode = "3600", SystemRole = GlSystemRole.OpeningBalanceEquity },
                new() { Code = "4000", Name = "Professional Services & Consulting Revenue", NameFr = "Revenus de services professionnels et consultation", Type = GlAccountType.Revenue, SubType = GlAccountSubType.OperatingRevenue, StandardTaxCode = "8000", SystemRole = GlSystemRole.ServiceRevenue },
                new() { Code = "4100", Name = "Project Materials & Equipment Sales Revenue", NameFr = "Revenus de vente de matériel et équipements de projet", Type = GlAccountType.Revenue, SubType = GlAccountSubType.OperatingRevenue, StandardTaxCode = "8000", SystemRole = GlSystemRole.MaterialRevenue },
                new() { Code = "4800", Name = "Interest & Miscellaneous Income", NameFr = "Revenus d'intérêts et revenus divers", Type = GlAccountType.Revenue, SubType = GlAccountSubType.OtherIncome, StandardTaxCode = "8090", SystemRole = GlSystemRole.InterestAndOtherIncome },
                new() { Code = "5100", Name = "Direct Project Materials & Supplies (COGS)", NameFr = "Achats directs de matériel et fournitures de projet (CMV)", Type = GlAccountType.Expense, SubType = GlAccountSubType.DirectCost, StandardTaxCode = "8320", SystemRole = GlSystemRole.DirectProjectPurchases },
                new() { Code = "5200", Name = "Subcontracts & External Consultants", NameFr = "Sous-traitance et consultants externes", Type = GlAccountType.Expense, SubType = GlAccountSubType.DirectCost, StandardTaxCode = "8360", SystemRole = GlSystemRole.DirectSubcontractors },
                new() { Code = "5300", Name = "Sales Commissions Expense", NameFr = "Frais de commissions sur ventes", Type = GlAccountType.Expense, SubType = GlAccountSubType.DirectCost, StandardTaxCode = "9061", SystemRole = GlSystemRole.CommissionsExpense },
                new() { Code = "5400", Name = "Gross Wages, Salaries & Vacation Expense", NameFr = "Salaires bruts, traitements et indemnités de vacances", Type = GlAccountType.Expense, SubType = GlAccountSubType.PayrollExpense, StandardTaxCode = "9060", SystemRole = GlSystemRole.WagesAndSalariesExpense },
                new() { Code = "5420", Name = "Employer Payroll Taxes & Benefits (RRQ, RQAP, AE, FSS, CNESST, CNT)", NameFr = "Charges sociales de l'employeur (RRQ, RQAP, AE, FSS, CNESST, CNT)", Type = GlAccountType.Expense, SubType = GlAccountSubType.PayrollExpense, StandardTaxCode = "8622", SystemRole = GlSystemRole.EmployerPayrollTaxesExpense },
                new() { Code = "5600", Name = "Office, General & Administrative Overhead", NameFr = "Frais de bureau et frais généraux d'administration", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8810", SystemRole = GlSystemRole.OfficeAndOverheadExpense },
                new() { Code = "5610", Name = "Rent, Electricity & Utilities", NameFr = "Loyer, électricité (Hydro-Québec) et services publics", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8911", SystemRole = GlSystemRole.RentAndUtilitiesExpense },
                new() { Code = "5620", Name = "Telecommunications & Internet", NameFr = "Télécommunications et Internet", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "9225" },
                new() { Code = "5630", Name = "Commercial Insurance", NameFr = "Assurances commerciales", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8690" },
                new() { Code = "5640", Name = "Legal, Accounting & Professional Fees", NameFr = "Honoraires professionnels (Comptabilité et juridique)", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8860" },
                new() { Code = "5650", Name = "Software Subscriptions & Cloud Hosting (SaaS)", NameFr = "Logiciels, abonnements infonuagiques et licences", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8813", SystemRole = GlSystemRole.SoftwareAndSubscriptionsExpense },
                new() { Code = "5660", Name = "Amortization of Tangible Assets", NameFr = "Amortissement des immobilisations corporelles", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8670" },
                new() { Code = "5670", Name = "Advertising, Marketing & Promotion", NameFr = "Publicité, marketing et promotion", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8520" },
                new() { Code = "5700", Name = "Travel & Kilometrage Reimbursements", NameFr = "Frais de déplacement et allocations kilométriques", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "9200", SystemRole = GlSystemRole.TravelAndMileageExpense },
                new() { Code = "5710", Name = "Meals & Entertainment (50% Deductible)", NameFr = "Repas et frais de représentation (déductibles à 50 %)", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8523" },
                new() { Code = "5800", Name = "Bank Service Charges & Interest Expense", NameFr = "Frais bancaires et intérêts", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8710", SystemRole = GlSystemRole.BankChargesAndInterest },
                new() { Code = "5850", Name = "Foreign Exchange Gain / Loss", NameFr = "Gain ou perte sur taux de change", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8231", SystemRole = GlSystemRole.ForeignExchangeGainLoss },
                new() { Code = "5860", Name = "Bad Debts & Balance Write-Offs", NameFr = "Créances irrécouvrables et radiations de soldes", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "8590", SystemRole = GlSystemRole.BadDebtsAndWriteOffs },
                new() { Code = "5900", Name = "Corporate Income Tax Expense (T2 / CO-17)", NameFr = "Impôts sur le revenu des sociétés (T2 / CO-17)", Type = GlAccountType.Expense, SubType = GlAccountSubType.Overhead, StandardTaxCode = "9990" }
            ],
            SalesTaxes =
            [
                new() { Code = "TPS", Name = "GST / TPS (5%)", NameFr = "TPS (5 %)", Rate = 0.05m, IsRecoverable = true, IsCompound = false, AuthorityName = "Revenu Québec / CRA", RegistrationNumberLabel = "GST / TPS Registration #", CollectedLiabilityGlCode = "2160", RecoverableAssetGlCode = "2165" },
                new() { Code = "TVQ", Name = "QST / TVQ (9.975%)", NameFr = "TVQ (9,975 %)", Rate = 0.09975m, IsRecoverable = true, IsCompound = false, AuthorityName = "Revenu Québec", RegistrationNumberLabel = "QST / TVQ Registration #", CollectedLiabilityGlCode = "2170", RecoverableAssetGlCode = "2175" }
            ],
            MileageRules = new MileageAllowanceRule
            {
                PrimaryRatePerKm = 0.73m,
                TierThresholdKm = 5000m,
                SecondaryRatePerKm = 0.67m
            },
            PayrollRules = new JurisdictionPayrollRules
            {
                DefaultVacationRate = 0.04m,
                SeniorVacationRate = 0.06m,
                SeniorityYearsForEnhancedVacation = 3,
                EmployeeParameters =
                [
                    new()
                    {
                        Key = "TD1_FEDERAL_AMOUNT",
                        Label = "TD1 Federal Personal Tax Credit Amount",
                        LabelFr = "TD1 Montant personnel de base fédéral",
                        HelpText = "Annual Federal total claim amount from CRA Form TD1 (2026 Basic Personal Amount is $16,452, phased out to $14,829 above $181,440).",
                        HelpTextFr = "Montant annuel total des crédits réclamé sur le formulaire TD1 de l'ARC (le montant personnel de base 2026 est de 16 452 $, réduit jusqu'à 14 829 $ au-delà de 181 440 $).",
                        DataType = EmployeeRuleParameterType.Currency,
                        DefaultValue = "16452",
                        ScopeCode = "FED_CA_QC"
                    },
                    new()
                    {
                        Key = "TP1015_QC_AMOUNT",
                        Label = "TP-1015.3 Québec Personal Tax Credit Amount",
                        LabelFr = "TP-1015.3 Montant personnel de base du Québec",
                        HelpText = "Annual Québec personal tax credits claimed on Revenu Québec Form TP-1015.3-V Line 10 (2026 Basic Personal Amount is $18,952).",
                        HelpTextFr = "Montant annuel des crédits d'impôt personnels inscrit à la ligne 10 du formulaire TP-1015.3 de Revenu Québec (le montant personnel de base 2026 est de 18 952 $).",
                        DataType = EmployeeRuleParameterType.Currency,
                        DefaultValue = "18952",
                        ScopeCode = "PROV_QC"
                    },
                    new()
                    {
                        Key = "EXTRA_TAX_FED",
                        Label = "Additional Federal Income Tax / Pay",
                        LabelFr = "Impôt fédéral volontaire additionnel / paie",
                        DataType = EmployeeRuleParameterType.Currency,
                        DefaultValue = "0",
                        ScopeCode = "FED_CA_QC"
                    },
                    new()
                    {
                        Key = "EXTRA_TAX_QC",
                        Label = "Additional Québec Income Tax / Pay",
                        LabelFr = "Impôt du Québec volontaire additionnel / paie",
                        DataType = EmployeeRuleParameterType.Currency,
                        DefaultValue = "0",
                        ScopeCode = "PROV_QC"
                    }
                ],
                Contributions =
                [
                    new() { Code = "QPP", Name = "Québec Pension Plan (QPP / RRQ Base 5.30% + 1st Add. 1.00%)", NameFr = "Régime de rentes du Québec (RRQ de base 5,30 % + 1er suppl. 1,00 %)", Authority = "Revenu Québec", AnnualExemption = 3500m, AnnualMaxInsurableEarnings = 74600m, ApplyAnnualCap = true, EmployeeRate = 0.063m, EmployerRate = 0.063m, DeductibleEmployeeRate = 0.01m, GrantsBasicTaxCredit = true, SlipBoxCode = "RL-1 Box B.A / T4 Box 17", LiabilityGlCode = "2210", ExpenseGlCode = "5420" },
                    new() { Code = "QPP2", Name = "Québec Pension Plan Tier 2 (QPP2 / RRQ2 — 4%)", NameFr = "Régime de rentes du Québec 2e palier (RRQ2 — 4 %)", Authority = "Revenu Québec", AnnualExemption = 0m, AnnualFloorEarnings = 74600m, AnnualMaxInsurableEarnings = 85000m, ApplyAnnualCap = true, EmployeeRate = 0.04m, EmployerRate = 0.04m, IsTaxDeductibleFromGross = true, GrantsBasicTaxCredit = false, SlipBoxCode = "RL-1 Box B.B / T4 Box 17A", LiabilityGlCode = "2210", ExpenseGlCode = "5420" },
                    new() { Code = "QPIP", Name = "Québec Parental Insurance Plan (QPIP / RQAP)", NameFr = "Régime québécois d'assurance parentale (RQAP)", Authority = "Revenu Québec", AnnualExemption = 0m, AnnualMaxInsurableEarnings = 103000m, ApplyAnnualCap = true, EmployeeRate = 0.0043m, EmployerRate = 0.00602m, GrantsBasicTaxCredit = true, SlipBoxCode = "RL-1 Box H / T4 Box 55", LiabilityGlCode = "2210", ExpenseGlCode = "5420" },
                    new() { Code = "EI_QC", Name = "Employment Insurance — Québec Rate (EI / AE)", NameFr = "Assurance-emploi — Taux du Québec (AE)", Authority = "CRA", AnnualExemption = 0m, AnnualMaxInsurableEarnings = 68900m, ApplyAnnualCap = true, EmployeeRate = 0.013m, EmployerRate = 0.0182m, GrantsBasicTaxCredit = true, SlipBoxCode = "T4 Box 18 / RL-1 Box C", LiabilityGlCode = "2210", ExpenseGlCode = "5420" },
                    new() { Code = "FSS", Name = "Health Services Fund (HSF / FSS Québec — SMB Service Rate)", NameFr = "Fonds des services de santé (FSS — PME secteur des services)", Authority = "Revenu Québec", AnnualExemption = 0m, ApplyAnnualCap = false, EmployeeRate = 0m, EmployerRate = 0.0165m, GrantsBasicTaxCredit = false, SlipBoxCode = "RLZ-1.S FSS", LiabilityGlCode = "2210", ExpenseGlCode = "5420" },
                    new() { Code = "CNESST", Name = "CNESST Workplace Health & Safety (Employer Average Rate)", NameFr = "CNESST — Santé et sécurité du travail (Taux moyen employeur)", Authority = "Revenu Québec", AnnualExemption = 0m, AnnualMaxInsurableEarnings = 103000m, ApplyAnnualCap = true, EmployeeRate = 0m, EmployerRate = 0.0154m, GrantsBasicTaxCredit = false, SlipBoxCode = "CNESST Versement", LiabilityGlCode = "2210", ExpenseGlCode = "5420" },
                    new() { Code = "CNT", Name = "Labour Standards Contribution (CNT — 0.06%)", NameFr = "Cotisation relative aux normes du travail (CNT — 0,06 %)", Authority = "Revenu Québec", AnnualExemption = 0m, AnnualMaxInsurableEarnings = 103000m, ApplyAnnualCap = true, EmployeeRate = 0m, EmployerRate = 0.0006m, GrantsBasicTaxCredit = false, SlipBoxCode = "LE-39.0.2 CNT", LiabilityGlCode = "2210", ExpenseGlCode = "5420" }
                ],
                IncomeTaxTables =
                [
                    new()
                    {
                        Code = "FED_CA_QC",
                        Name = "Federal Income Tax (with 16.5% Québec Abatement)",
                        NameFr = "Impôt fédéral sur le revenu (avec abattement du Québec de 16,5 %)",
                        Authority = "CRA",
                        Method = TaxComputationMethod.ProgressiveBrackets,
                        SlipBoxCode = "T4 Box 22",
                        LiabilityGlCode = "2210",
                        Brackets =
                        [
                            new() { UpToAnnualIncome = 58523m, Rate = 0.14m },
                            new() { UpToAnnualIncome = 117045m, Rate = 0.205m },
                            new() { UpToAnnualIncome = 181440m, Rate = 0.26m },
                            new() { UpToAnnualIncome = 258482m, Rate = 0.29m },
                            new() { UpToAnnualIncome = null, Rate = 0.33m }
                        ],
                        PostSteps =
                        [
                            new() { StepType = TaxAdjustmentStepType.SubtractNonRefundableCredit, Label = "TD1 Personal & Base Contribution Credit (14%)", ParameterKey = "TD1_FEDERAL_AMOUNT", DefaultAmount = 16452m, MinimumAmountAfterPhaseOut = 14829m, PhaseOutStartIncome = 181440m, PhaseOutEndIncome = 258482m, RateOrFactor = 0.14m, IncludeCreditableContributions = true },
                            new() { StepType = TaxAdjustmentStepType.SubtractNonRefundableCredit, Label = "Canada Employment Amount Credit (CEA — 14%)", DefaultAmount = 1501m, CapAtAnnualGrossIncome = true, RateOrFactor = 0.14m, IncludeCreditableContributions = false },
                            new() { StepType = TaxAdjustmentStepType.MultiplyByFactor, Label = "Québec Federal Tax Abatement (-16.5%)", RateOrFactor = -0.165m },
                            new() { StepType = TaxAdjustmentStepType.AddFlatParameter, Label = "Voluntary Extra Federal Tax", ParameterKey = "EXTRA_TAX_FED" }
                        ]
                    },
                    new()
                    {
                        Code = "PROV_QC",
                        Name = "Québec Provincial Income Tax (Revenu Québec)",
                        NameFr = "Impôt provincial du Québec (Revenu Québec)",
                        Authority = "Revenu Québec",
                        Method = TaxComputationMethod.ProgressiveBrackets,
                        PreBracketPercentageDeductionRate = 0.06m,
                        PreBracketPercentageDeductionCap = 1450m,
                        SlipBoxCode = "RL-1 Box E",
                        LiabilityGlCode = "2210",
                        Brackets =
                        [
                            new() { UpToAnnualIncome = 54345m, Rate = 0.14m },
                            new() { UpToAnnualIncome = 108680m, Rate = 0.19m },
                            new() { UpToAnnualIncome = 132245m, Rate = 0.24m },
                            new() { UpToAnnualIncome = null, Rate = 0.2575m }
                        ],
                        PostSteps =
                        [
                            new() { StepType = TaxAdjustmentStepType.SubtractNonRefundableCredit, Label = "TP-1015.3 Personal Tax Credit (14%)", ParameterKey = "TP1015_QC_AMOUNT", DefaultAmount = 18952m, RateOrFactor = 0.14m, IncludeCreditableContributions = false },
                            new() { StepType = TaxAdjustmentStepType.AddFlatParameter, Label = "Voluntary Extra Québec Tax", ParameterKey = "EXTRA_TAX_QC" }
                        ]
                    }
                ]
            }
        };
    }
}
