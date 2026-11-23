namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

public class FinancialAccountRepository : JsonRepository<FinancialAccount>
{
    public FinancialAccountRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "FinancialAccounts"), "*.json")
    {
    }

    protected override string GetFilePath(FinancialAccount item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public List<FinancialAccount> GetActive()
    {
        return GetAll()
            .Where(a => a.IsActive)
            .OrderByDescending(a => a.IsDefaultOperating)
            .ThenByDescending(a => a.IsDefaultCreditCard)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public FinancialAccount? GetByGlCode(string? glCode)
    {
        if (string.IsNullOrWhiteSpace(glCode)) return null;
        var trimmed = glCode.Trim();
        return GetAll().FirstOrDefault(a => string.Equals(a.LinkedGlAccountCode, trimmed, StringComparison.OrdinalIgnoreCase) && a.IsActive)
            ?? GetAll().FirstOrDefault(a => string.Equals(a.LinkedGlAccountCode, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public FinancialAccount? GetDefaultOperating()
    {
        var active = GetActive();
        return active.FirstOrDefault(a => a.IsDefaultOperating)
            ?? active.FirstOrDefault(a => string.Equals(a.AccountType, FinancialAccountType.Chequing, StringComparison.OrdinalIgnoreCase))
            ?? active.FirstOrDefault(a => FinancialAccountType.IsAssetType(a.AccountType))
            ?? active.FirstOrDefault();
    }

    public FinancialAccount? GetDefaultCreditCard()
    {
        var active = GetActive();
        return active.FirstOrDefault(a => a.IsDefaultCreditCard)
            ?? active.FirstOrDefault(a => string.Equals(a.AccountType, FinancialAccountType.CreditCard, StringComparison.OrdinalIgnoreCase));
    }
}
