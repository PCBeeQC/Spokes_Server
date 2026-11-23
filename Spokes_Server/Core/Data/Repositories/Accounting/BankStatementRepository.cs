namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

public class BankStatementRepository : JsonRepository<BankStatement>
{
    public BankStatementRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "BankStatements"), "*.json")
    {
    }

    protected override string GetFilePath(BankStatement item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public List<BankStatement> GetByAccount(string glAccountCode) =>
        GetAll()
            .Where(s => string.Equals(s.GlAccountCode, glAccountCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.EndDate)
            .ToList();
}
