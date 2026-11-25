namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

public class BankRuleRepository : JsonRepository<BankRule>
{
    public BankRuleRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "BankRules"), "*.json")
    {
    }

    protected override string GetFilePath(BankRule item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public List<BankRule> GetOrderedActive() =>
        GetAll()
            .Where(r => r.IsActive && !string.IsNullOrWhiteSpace(r.Keywords))
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .ToList();

    public List<BankRule> GetOrderedAll() =>
        GetAll()
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .ToList();
}
