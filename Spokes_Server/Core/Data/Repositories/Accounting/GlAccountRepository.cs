namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

public class GlAccountRepository : JsonRepository<GlAccount>
{
    public GlAccountRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "GlAccounts"), "*.json")
    {
    }

    protected override string GetFilePath(GlAccount item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public GlAccount? GetByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return GetAll().FirstOrDefault(a => string.Equals(a.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public GlAccount? GetBySystemRole(string systemRole)
    {
        if (string.IsNullOrWhiteSpace(systemRole)) return null;
        return GetAll().FirstOrDefault(a => string.Equals(a.SystemRole, systemRole, StringComparison.OrdinalIgnoreCase) && a.IsActive)
            ?? GetAll().FirstOrDefault(a => string.Equals(a.SystemRole, systemRole, StringComparison.OrdinalIgnoreCase));
    }

    public List<GlAccount> GetActive()
    {
        return GetAll()
            .Where(a => a.IsActive)
            .OrderBy(a => a.Code)
            .ToList();
    }
}
