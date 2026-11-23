namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;

public interface IFinancialAccountService
{
    /// <summary>
    /// Idempotently ensures at least the default Operating Bank financial account is bootstrapped
    /// from the Chart of Accounts (along with any other Bank/Card/LOC GL accounts that already have activity).
    /// </summary>
    void EnsureBootstrapped();

    List<FinancialAccount> GetActiveAccounts(string[]? allowedTypes = null);
    List<FinancialAccount> GetAllAccounts(bool includeArchived = true);
    FinancialAccount? GetById(string? id);
    FinancialAccount? GetByGlCode(string? glCode);

    /// <summary>
    /// Creates a new Financial Account. By default automatically allocates and persists a dedicated 1:1
    /// backing <see cref="GlAccount"/>; optionally links to an existing unlinked GL account when
    /// <paramref name="existingGlAccountCode"/> is provided by an accountant/power user.
    /// </summary>
    SpokesResult<FinancialAccount> CreateAccount(FinancialAccount account, string? existingGlAccountCode = null);

    /// <summary>
    /// Updates an existing Financial Account, keeping its backing <see cref="GlAccount"/> name, currency,
    /// and system role synchronized.
    /// </summary>
    SpokesResult<FinancialAccount> UpdateAccount(FinancialAccount updated, string? existingGlAccountCode = null);

    /// <summary>
    /// Soft-archives a Financial Account so it disappears from day-to-day pickers without breaking historical ledger data.
    /// </summary>
    SpokesResult<FinancialAccount> ArchiveAccount(string accountId);

    /// <summary>
    /// Restores an archived Financial Account.
    /// </summary>
    SpokesResult<FinancialAccount> RestoreAccount(string accountId);

    /// <summary>
    /// Attaches (or updates) an operating Line of Credit limit directly on an existing Chequing account.
    /// </summary>
    SpokesResult<FinancialAccount> AttachCreditLineToChequing(string chequingAccountId, decimal creditLimit, string? notes = null);

    /// <summary>
    /// Resolves the default backing GL account code for a given payment method
    /// ("Credit Card" -> Default Credit Card, "Cash" -> Cash on Hand, otherwise Default Operating Bank).
    /// </summary>
    string ResolveDefaultGlCodeForMethod(string? paymentMethod);

    /// <summary>
    /// Resolves a sensible default payment method for the selected Financial Account
    /// (e.g. Chequing/Savings/LOC/Loan -> bankDefaultMethod such as "Wire Transfer", CreditCard -> "Credit Card", CashOnHand -> "Cash").
    /// </summary>
    string ResolveDefaultPaymentMethodForAccount(string? glCode, string bankDefaultMethod = "Wire Transfer");

    /// <summary>
    /// Resolves the friendly real-world display label for a given GL account code (never includes GL codes).
    /// </summary>
    string GetDisplayLabelByGlCode(string? glCode);

    /// <summary>
    /// Computes live book balances keyed by <see cref="FinancialAccount.Id"/> as of <paramref name="asOfDate"/>.
    /// For asset accounts, positive means cash on hand and negative means overdraft / drawn credit line.
    /// For liability accounts (Credit Card, standalone Line of Credit, Loan), positive means amount owed.
    /// </summary>
    Dictionary<string, decimal> GetLiveBalances(DateTime? asOfDate = null);
}

public class FinancialAccountService : IFinancialAccountService
{
    private readonly Database _db;
    private readonly IJurisdictionTemplateService _jurisdictionService;
    private readonly ILedgerService _ledgerService;
    private readonly object _bootstrapLock = new();

    public FinancialAccountService(
        Database db,
        IJurisdictionTemplateService jurisdictionService,
        ILedgerService ledgerService)
    {
        _db = db;
        _jurisdictionService = jurisdictionService;
        _ledgerService = ledgerService;
    }

    public void EnsureBootstrapped()
    {
        if (_db.FinancialAccounts == null) return;

        lock (_bootstrapLock)
        {
            _jurisdictionService.EnsureChartOfAccountsSeeded();

            if (_db.FinancialAccounts.GetAll().Count > 0)
            {
                return;
            }

            var glAccounts = _db.GlAccounts?.GetAll() ?? [];
            if (glAccounts.Count == 0) return;

            var activeGlWithActivity = CollectGlCodesWithActivity();
            var defaultCurrency = _jurisdictionService.GetActiveTemplate().DefaultCurrency;

            // 1. Always bootstrap the primary Operating Bank account (1020)
            var operatingGl = glAccounts.FirstOrDefault(a => string.Equals(a.SystemRole, GlSystemRole.OperatingBank, StringComparison.OrdinalIgnoreCase))
                ?? glAccounts.FirstOrDefault(a => string.Equals(a.Code, "1020", StringComparison.OrdinalIgnoreCase))
                ?? glAccounts.FirstOrDefault(a => string.Equals(a.SubType, GlAccountSubType.Bank, StringComparison.OrdinalIgnoreCase) && a.IsActive);

            var createdCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (operatingGl != null)
            {
                var operating = new FinancialAccount
                {
                    Name = NormalizeFriendlyName(operatingGl),
                    AccountType = FinancialAccountType.Chequing,
                    Currency = string.IsNullOrWhiteSpace(operatingGl.Currency) ? defaultCurrency : operatingGl.Currency,
                    LinkedGlAccountCode = operatingGl.Code,
                    IsDefaultOperating = true,
                    IsActive = true
                };
                _db.FinancialAccounts.Save(operating);
                createdCodes.Add(operatingGl.Code);
            }

            // 2. Bootstrap any other Bank/CreditCard/LineOfCredit/Loan GL accounts that already have activity
            //    (plus the default Corporate Credit Card if it has activity, so existing workspaces transition seamlessly
            //    without cluttering brand-new workspaces with unused Petty Cash / Savings accounts).
            foreach (var gl in glAccounts.Where(a => a.IsActive && GlAccountSubType.IsCashOrCardPaymentAccount(a.SubType)))
            {
                if (createdCodes.Contains(gl.Code)) continue;
                if (!activeGlWithActivity.Contains(gl.Code)) continue;

                var accountType = InferFinancialAccountType(gl);
                var isDefaultCard = string.Equals(gl.SystemRole, GlSystemRole.CorporateCreditCard, StringComparison.OrdinalIgnoreCase)
                    || (accountType == FinancialAccountType.CreditCard && _db.FinancialAccounts.GetDefaultCreditCard() == null);

                var fa = new FinancialAccount
                {
                    Name = NormalizeFriendlyName(gl),
                    AccountType = accountType,
                    Currency = string.IsNullOrWhiteSpace(gl.Currency) ? defaultCurrency : gl.Currency,
                    LinkedGlAccountCode = gl.Code,
                    IsDefaultCreditCard = isDefaultCard,
                    IsActive = true
                };
                _db.FinancialAccounts.Save(fa);
                createdCodes.Add(gl.Code);
            }
        }
    }

    public List<FinancialAccount> GetActiveAccounts(string[]? allowedTypes = null)
    {
        EnsureBootstrapped();
        IEnumerable<FinancialAccount> query = _db.FinancialAccounts?.GetActive() ?? [];
        if (allowedTypes is { Length: > 0 })
        {
            query = query.Where(a => allowedTypes.Any(t => string.Equals(t, a.AccountType, StringComparison.OrdinalIgnoreCase)));
        }
        return query.ToList();
    }

    public List<FinancialAccount> GetAllAccounts(bool includeArchived = true)
    {
        EnsureBootstrapped();
        var all = _db.FinancialAccounts?.GetAll() ?? [];
        if (!includeArchived)
        {
            all = all.Where(a => a.IsActive).ToList();
        }
        return all
            .OrderByDescending(a => a.IsActive)
            .ThenByDescending(a => a.IsDefaultOperating)
            .ThenByDescending(a => a.IsDefaultCreditCard)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public FinancialAccount? GetById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureBootstrapped();
        return _db.FinancialAccounts?.GetById(id.Trim());
    }

    public FinancialAccount? GetByGlCode(string? glCode)
    {
        if (string.IsNullOrWhiteSpace(glCode)) return null;
        EnsureBootstrapped();
        return _db.FinancialAccounts?.GetByGlCode(glCode);
    }

    public SpokesResult<FinancialAccount> CreateAccount(FinancialAccount account, string? existingGlAccountCode = null)
    {
        EnsureBootstrapped();

        var validationError = ValidateAccountFields(account);
        if (validationError != null)
        {
            return SpokesResult<FinancialAccount>.Failure(validationError);
        }

        account.Id = string.IsNullOrWhiteSpace(account.Id) ? Guid.NewGuid().ToString() : account.Id.Trim();
        account.Name = account.Name.Trim();
        account.Currency = NormalizeCurrency(account.Currency);
        account.InstitutionName = (account.InstitutionName ?? string.Empty).Trim();
        account.Last4Digits = NormalizeLast4(account.Last4Digits);
        account.Notes = (account.Notes ?? string.Empty).Trim();
        account.CreditLimit = FinancialAccountType.SupportsCreditLimit(account.AccountType) && account.CreditLimit is > 0m
            ? Math.Round(account.CreditLimit.Value, 2)
            : null;
        account.IsActive = true;
        account.CreatedAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;

        // Enforce default flags only on eligible types
        if (!FinancialAccountType.IsAssetType(account.AccountType))
        {
            account.IsDefaultOperating = false;
        }
        if (!string.Equals(account.AccountType, FinancialAccountType.CreditCard, StringComparison.OrdinalIgnoreCase))
        {
            account.IsDefaultCreditCard = false;
        }

        // Auto-promote first Credit Card to default credit card if none exists
        if (string.Equals(account.AccountType, FinancialAccountType.CreditCard, StringComparison.OrdinalIgnoreCase) &&
            _db.FinancialAccounts.GetDefaultCreditCard() == null)
        {
            account.IsDefaultCreditCard = true;
        }

        // Resolve or auto-create backing 1:1 GL Account
        var glResult = ResolveOrCreateBackingGlAccount(account, existingGlAccountCode, excludeAccountId: null);
        if (!glResult.IsSuccess || glResult.Value == null)
        {
            return SpokesResult<FinancialAccount>.Failure(glResult.Error ?? "Failed to link General Ledger account.");
        }

        account.LinkedGlAccountCode = glResult.Value.Code;

        ApplyDefaultExclusivityAndSystemRoles(account, glResult.Value);
        _db.FinancialAccounts.Save(account);

        return SpokesResult<FinancialAccount>.Success(account);
    }

    public SpokesResult<FinancialAccount> UpdateAccount(FinancialAccount updated, string? existingGlAccountCode = null)
    {
        EnsureBootstrapped();

        if (updated == null || string.IsNullOrWhiteSpace(updated.Id))
        {
            return SpokesResult<FinancialAccount>.Failure("Account ID is required.");
        }

        var existing = _db.FinancialAccounts?.GetById(updated.Id);
        if (existing == null)
        {
            return SpokesResult<FinancialAccount>.Failure("Financial account not found.");
        }

        var validationError = ValidateAccountFields(updated);
        if (validationError != null)
        {
            return SpokesResult<FinancialAccount>.Failure(validationError);
        }

        existing.Name = updated.Name.Trim();
        existing.AccountType = updated.AccountType;
        existing.Currency = NormalizeCurrency(updated.Currency);
        existing.InstitutionName = (updated.InstitutionName ?? string.Empty).Trim();
        existing.Last4Digits = NormalizeLast4(updated.Last4Digits);
        existing.Notes = (updated.Notes ?? string.Empty).Trim();
        existing.CreditLimit = FinancialAccountType.SupportsCreditLimit(existing.AccountType) && updated.CreditLimit is > 0m
            ? Math.Round(updated.CreditLimit.Value, 2)
            : null;
        existing.IsDefaultOperating = FinancialAccountType.IsAssetType(existing.AccountType) && updated.IsDefaultOperating;
        existing.IsDefaultCreditCard = string.Equals(existing.AccountType, FinancialAccountType.CreditCard, StringComparison.OrdinalIgnoreCase) && updated.IsDefaultCreditCard;
        existing.UpdatedAt = DateTime.UtcNow;

        // If an explicit existing GL account was requested and differs from current link, validate & re-link
        GlAccount? backingGl = null;
        if (!string.IsNullOrWhiteSpace(existingGlAccountCode) &&
            !string.Equals(existingGlAccountCode.Trim(), existing.LinkedGlAccountCode, StringComparison.OrdinalIgnoreCase))
        {
            var relinkResult = ResolveOrCreateBackingGlAccount(existing, existingGlAccountCode, excludeAccountId: existing.Id);
            if (!relinkResult.IsSuccess || relinkResult.Value == null)
            {
                return SpokesResult<FinancialAccount>.Failure(relinkResult.Error ?? "Unable to link selected GL account.");
            }
            existing.LinkedGlAccountCode = relinkResult.Value.Code;
            backingGl = relinkResult.Value;
        }
        else
        {
            backingGl = _db.GlAccounts?.GetByCode(existing.LinkedGlAccountCode);
            if (backingGl == null)
            {
                var recreate = ResolveOrCreateBackingGlAccount(existing, null, excludeAccountId: existing.Id);
                if (!recreate.IsSuccess || recreate.Value == null)
                {
                    return SpokesResult<FinancialAccount>.Failure(recreate.Error ?? "Unable to create backing GL account.");
                }
                existing.LinkedGlAccountCode = recreate.Value.Code;
                backingGl = recreate.Value;
            }
            else
            {
                // Sync name, currency, and subtype to backing GL account
                backingGl.Name = existing.Name;
                backingGl.NameFr = existing.Name;
                backingGl.Currency = existing.Currency;
                backingGl.Type = FinancialAccountType.GetGlAccountType(existing.AccountType);
                backingGl.SubType = FinancialAccountType.GetGlAccountSubType(existing.AccountType);
                _db.GlAccounts?.Save(backingGl);
            }
        }

        ApplyDefaultExclusivityAndSystemRoles(existing, backingGl);
        _db.FinancialAccounts?.Save(existing);

        return SpokesResult<FinancialAccount>.Success(existing);
    }

    public SpokesResult<FinancialAccount> AttachCreditLineToChequing(string chequingAccountId, decimal creditLimit, string? notes = null)
    {
        EnsureBootstrapped();

        if (creditLimit <= 0m)
        {
            return SpokesResult<FinancialAccount>.Failure("Credit limit must be greater than zero.");
        }

        var chequing = _db.FinancialAccounts?.GetById(chequingAccountId);
        if (chequing == null || !chequing.IsActive)
        {
            return SpokesResult<FinancialAccount>.Failure("Target chequing account not found.");
        }

        if (!string.Equals(chequing.AccountType, FinancialAccountType.Chequing, StringComparison.OrdinalIgnoreCase))
        {
            return SpokesResult<FinancialAccount>.Failure("An attached operating line of credit can only be linked to a Chequing account.");
        }

        chequing.CreditLimit = Math.Round(creditLimit, 2);
        if (!string.IsNullOrWhiteSpace(notes))
        {
            chequing.Notes = string.IsNullOrWhiteSpace(chequing.Notes)
                ? notes.Trim()
                : $"{chequing.Notes}\n{notes.Trim()}";
        }
        chequing.UpdatedAt = DateTime.UtcNow;
        _db.FinancialAccounts?.Save(chequing);

        return SpokesResult<FinancialAccount>.Success(chequing);
    }

    public SpokesResult<FinancialAccount> ArchiveAccount(string accountId)
    {
        EnsureBootstrapped();
        var account = _db.FinancialAccounts?.GetById(accountId);
        if (account == null)
        {
            return SpokesResult<FinancialAccount>.Failure("Financial account not found.");
        }

        var activeOthers = _db.FinancialAccounts!.GetActive().Where(a => a.Id != account.Id).ToList();
        if (activeOthers.Count == 0)
        {
            return SpokesResult<FinancialAccount>.Failure("You must keep at least one active financial account.");
        }

        if (account.IsDefaultOperating)
        {
            var replacement = activeOthers.FirstOrDefault(a => string.Equals(a.AccountType, FinancialAccountType.Chequing, StringComparison.OrdinalIgnoreCase))
                ?? activeOthers.FirstOrDefault(a => FinancialAccountType.IsAssetType(a.AccountType));
            if (replacement == null)
            {
                return SpokesResult<FinancialAccount>.Failure("Assign another operating bank account as default before archiving this one.");
            }
            replacement.IsDefaultOperating = true;
            replacement.UpdatedAt = DateTime.UtcNow;
            var replacementGl = _db.GlAccounts?.GetByCode(replacement.LinkedGlAccountCode);
            if (replacementGl != null)
            {
                ApplyDefaultExclusivityAndSystemRoles(replacement, replacementGl);
            }
            _db.FinancialAccounts.Save(replacement);
        }

        account.IsActive = false;
        account.IsDefaultOperating = false;
        account.IsDefaultCreditCard = false;
        account.UpdatedAt = DateTime.UtcNow;
        _db.FinancialAccounts.Save(account);

        return SpokesResult<FinancialAccount>.Success(account);
    }

    public SpokesResult<FinancialAccount> RestoreAccount(string accountId)
    {
        EnsureBootstrapped();
        var account = _db.FinancialAccounts?.GetById(accountId);
        if (account == null)
        {
            return SpokesResult<FinancialAccount>.Failure("Financial account not found.");
        }

        account.IsActive = true;
        account.UpdatedAt = DateTime.UtcNow;
        _db.FinancialAccounts?.Save(account);

        var backingGl = _db.GlAccounts?.GetByCode(account.LinkedGlAccountCode);
        if (backingGl != null && !backingGl.IsActive)
        {
            backingGl.IsActive = true;
            _db.GlAccounts?.Save(backingGl);
        }

        return SpokesResult<FinancialAccount>.Success(account);
    }

    public string ResolveDefaultGlCodeForMethod(string? paymentMethod)
    {
        EnsureBootstrapped();

        if (string.Equals(paymentMethod, "Credit Card", StringComparison.OrdinalIgnoreCase))
        {
            var defaultCard = _db.FinancialAccounts?.GetDefaultCreditCard();
            if (defaultCard != null && !string.IsNullOrWhiteSpace(defaultCard.LinkedGlAccountCode))
            {
                return defaultCard.LinkedGlAccountCode;
            }
        }
        else if (string.Equals(paymentMethod, "Cash", StringComparison.OrdinalIgnoreCase))
        {
            var cashAccount = _db.FinancialAccounts?.GetActive()
                .FirstOrDefault(a => string.Equals(a.AccountType, FinancialAccountType.CashOnHand, StringComparison.OrdinalIgnoreCase));
            if (cashAccount != null && !string.IsNullOrWhiteSpace(cashAccount.LinkedGlAccountCode))
            {
                return cashAccount.LinkedGlAccountCode;
            }
        }

        var operating = _db.FinancialAccounts?.GetDefaultOperating();
        if (operating != null && !string.IsNullOrWhiteSpace(operating.LinkedGlAccountCode))
        {
            return operating.LinkedGlAccountCode;
        }

        return _db.GlAccounts?.GetBySystemRole(GlSystemRole.OperatingBank)?.Code ?? "1020";
    }

    public string ResolveDefaultPaymentMethodForAccount(string? glCode, string bankDefaultMethod = "Wire Transfer")
    {
        var account = GetByGlCode(glCode);
        return account?.AccountType switch
        {
            FinancialAccountType.CreditCard => "Credit Card",
            FinancialAccountType.CashOnHand => "Cash",
            FinancialAccountType.PaymentProcessor => "Credit Card",
            _ => bankDefaultMethod
        };
    }

    public string GetDisplayLabelByGlCode(string? glCode)
    {
        if (string.IsNullOrWhiteSpace(glCode)) return string.Empty;
        EnsureBootstrapped();

        var fa = _db.FinancialAccounts?.GetByGlCode(glCode);
        if (fa != null)
        {
            return fa.DisplayLabel;
        }

        // Graceful fallback for legacy unlinked GL accounts: still never expose raw GL code in day-to-day UI
        var gl = _db.GlAccounts?.GetByCode(glCode);
        return gl != null ? gl.GetLocalizedName() : glCode.Trim();
    }

    public Dictionary<string, decimal> GetLiveBalances(DateTime? asOfDate = null)
    {
        EnsureBootstrapped();
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var accounts = _db.FinancialAccounts?.GetAll() ?? [];
        if (accounts.Count == 0) return result;

        var cutoff = (asOfDate ?? DateTime.Today).Date;
        var fiscalStart = new DateTime(cutoff.Year, 1, 1);
        var tb = _ledgerService.CalculateTrialBalance(fiscalStart, cutoff, includeZeroAccounts: true);
        var byGlCode = tb.Rows.ToDictionary(r => r.AccountCode, r => r.EndingBalance, StringComparer.OrdinalIgnoreCase);

        foreach (var account in accounts)
        {
            var balance = !string.IsNullOrWhiteSpace(account.LinkedGlAccountCode) &&
                          byGlCode.TryGetValue(account.LinkedGlAccountCode, out var ending)
                ? ending
                : 0m;
            result[account.Id] = balance;
        }

        return result;
    }

    private string? ValidateAccountFields(FinancialAccount? account)
    {
        if (account == null) return "Account is required.";
        if (string.IsNullOrWhiteSpace(account.Name)) return "Account name is required.";
        if (!FinancialAccountType.All.Contains(account.AccountType, StringComparer.OrdinalIgnoreCase))
        {
            return $"Unsupported account type '{account.AccountType}'.";
        }
        if (account.CreditLimit is < 0m)
        {
            return "Credit limit cannot be negative.";
        }
        return null;
    }

    private SpokesResult<GlAccount> ResolveOrCreateBackingGlAccount(
        FinancialAccount account,
        string? existingGlAccountCode,
        string? excludeAccountId)
    {
        if (_db.GlAccounts == null)
        {
            return SpokesResult<GlAccount>.Failure("General Ledger account repository is unavailable.");
        }

        if (!string.IsNullOrWhiteSpace(existingGlAccountCode))
        {
            var trimmedCode = existingGlAccountCode.Trim();
            var targetGl = _db.GlAccounts.GetByCode(trimmedCode);
            if (targetGl == null)
            {
                return SpokesResult<GlAccount>.Failure($"GL account '{trimmedCode}' was not found.");
            }

            var conflicting = _db.FinancialAccounts.GetAll().FirstOrDefault(a =>
                a.IsActive &&
                a.Id != excludeAccountId &&
                string.Equals(a.LinkedGlAccountCode, trimmedCode, StringComparison.OrdinalIgnoreCase));
            if (conflicting != null)
            {
                return SpokesResult<GlAccount>.Failure($"GL account '{trimmedCode}' is already linked to '{conflicting.Name}'.");
            }

            if (!targetGl.IsActive)
            {
                targetGl.IsActive = true;
                _db.GlAccounts.Save(targetGl);
            }

            return SpokesResult<GlAccount>.Success(targetGl);
        }

        // Auto-allocate next unused GL code in the range for this account type
        var existingCodes = new HashSet<string>(
            _db.GlAccounts.GetAll().Select(a => a.Code),
            StringComparer.OrdinalIgnoreCase);

        var (rangeStart, rangeEnd) = FinancialAccountType.GetGlCodeRange(account.AccountType);
        string? allocatedCode = null;
        for (var code = rangeStart; code <= rangeEnd; code++)
        {
            var candidate = code.ToString();
            if (!existingCodes.Contains(candidate))
            {
                allocatedCode = candidate;
                break;
            }
        }

        if (allocatedCode == null)
        {
            var fallbackStart = FinancialAccountType.IsLiabilityType(account.AccountType) ? 2550 : 1050;
            for (var code = fallbackStart; code <= fallbackStart + 400; code++)
            {
                var candidate = code.ToString();
                if (!existingCodes.Contains(candidate))
                {
                    allocatedCode = candidate;
                    break;
                }
            }
        }

        if (allocatedCode == null)
        {
            return SpokesResult<GlAccount>.Failure("Unable to allocate a new General Ledger account code.");
        }

        var glAccount = new GlAccount
        {
            Code = allocatedCode,
            Name = account.Name,
            NameFr = account.Name,
            Type = FinancialAccountType.GetGlAccountType(account.AccountType),
            SubType = FinancialAccountType.GetGlAccountSubType(account.AccountType),
            StandardTaxCode = FinancialAccountType.GetDefaultStandardTaxCode(account.AccountType),
            Currency = account.Currency,
            IsSystemAccount = false,
            IsActive = true
        };

        _db.GlAccounts.Save(glAccount);
        return SpokesResult<GlAccount>.Success(glAccount);
    }

    private void ApplyDefaultExclusivityAndSystemRoles(FinancialAccount savedAccount, GlAccount? backingGl)
    {
        var allAccounts = _db.FinancialAccounts?.GetAll() ?? [];

        if (savedAccount.IsDefaultOperating)
        {
            foreach (var other in allAccounts.Where(a => a.Id != savedAccount.Id && a.IsDefaultOperating))
            {
                other.IsDefaultOperating = false;
                other.UpdatedAt = DateTime.UtcNow;
                _db.FinancialAccounts!.Save(other);
            }

            SyncGlSystemRole(GlSystemRole.OperatingBank, backingGl);
        }

        if (savedAccount.IsDefaultCreditCard)
        {
            foreach (var other in allAccounts.Where(a => a.Id != savedAccount.Id && a.IsDefaultCreditCard))
            {
                other.IsDefaultCreditCard = false;
                other.UpdatedAt = DateTime.UtcNow;
                _db.FinancialAccounts!.Save(other);
            }

            SyncGlSystemRole(GlSystemRole.CorporateCreditCard, backingGl);
        }
    }

    private void SyncGlSystemRole(string systemRole, GlAccount? targetGl)
    {
        if (_db.GlAccounts == null || targetGl == null) return;

        foreach (var otherGl in _db.GlAccounts.GetAll().Where(g =>
            g.Id != targetGl.Id && string.Equals(g.SystemRole, systemRole, StringComparison.OrdinalIgnoreCase)))
        {
            otherGl.SystemRole = GlSystemRole.None;
            _db.GlAccounts.Save(otherGl);
        }

        if (!string.Equals(targetGl.SystemRole, systemRole, StringComparison.OrdinalIgnoreCase))
        {
            targetGl.SystemRole = systemRole;
            _db.GlAccounts.Save(targetGl);
        }
    }

    private HashSet<string> CollectGlCodesWithActivity()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var je in _db.JournalEntries?.GetAll() ?? [])
        {
            foreach (var line in je.Lines)
            {
                if (!string.IsNullOrWhiteSpace(line.GlAccountCode))
                    codes.Add(line.GlAccountCode.Trim());
            }
        }

        foreach (var stmt in _db.BankStatements?.GetAll() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(stmt.GlAccountCode))
                codes.Add(stmt.GlAccountCode.Trim());
        }

        foreach (var tx in _db.DirectTransactions?.GetAll() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(tx.AccountGlCode))
                codes.Add(tx.AccountGlCode.Trim());
            if (!string.IsNullOrWhiteSpace(tx.DestinationAccountGlCode))
                codes.Add(tx.DestinationAccountGlCode.Trim());
        }

        foreach (var inv in _db.Invoices?.GetAll() ?? [])
        {
            foreach (var p in inv.Payments)
            {
                if (!string.IsNullOrWhiteSpace(p.BankGlAccountCode))
                    codes.Add(p.BankGlAccountCode.Trim());
            }
        }

        foreach (var bill in _db.Bills?.GetAll() ?? [])
        {
            foreach (var p in bill.Payments)
            {
                if (!string.IsNullOrWhiteSpace(p.BankGlAccountCode))
                    codes.Add(p.BankGlAccountCode.Trim());
            }
        }

        return codes;
    }

    private static string InferFinancialAccountType(GlAccount gl)
    {
        if (string.Equals(gl.SubType, GlAccountSubType.CreditCard, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(gl.SystemRole, GlSystemRole.CorporateCreditCard, StringComparison.OrdinalIgnoreCase))
        {
            return FinancialAccountType.CreditCard;
        }

        if (string.Equals(gl.SubType, GlAccountSubType.LineOfCredit, StringComparison.OrdinalIgnoreCase))
        {
            return FinancialAccountType.LineOfCredit;
        }

        if (string.Equals(gl.SubType, GlAccountSubType.Loan, StringComparison.OrdinalIgnoreCase))
        {
            return FinancialAccountType.Loan;
        }

        if (string.Equals(gl.SystemRole, GlSystemRole.PettyCash, StringComparison.OrdinalIgnoreCase))
        {
            return FinancialAccountType.CashOnHand;
        }

        if (gl.Name.Contains("saving", StringComparison.OrdinalIgnoreCase) ||
            gl.NameFr.Contains("épargne", StringComparison.OrdinalIgnoreCase))
        {
            return FinancialAccountType.Savings;
        }

        return FinancialAccountType.Chequing;
    }

    private static string NormalizeFriendlyName(GlAccount gl)
    {
        var name = gl.GetLocalizedName();
        return string.IsNullOrWhiteSpace(name) ? gl.Code : name.Trim();
    }

    private string NormalizeCurrency(string? currency)
    {
        var fallback = _jurisdictionService.GetActiveTemplate().DefaultCurrency;
        return string.IsNullOrWhiteSpace(currency) ? fallback : currency.Trim().ToUpperInvariant();
    }

    private static string NormalizeLast4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length > 8 ? trimmed[^8..] : trimmed;
    }
}
