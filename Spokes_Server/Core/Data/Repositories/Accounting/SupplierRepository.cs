using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class SupplierRepository : JsonRepository<Supplier>
{
    public SupplierRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "Suppliers"), "*.json")
    {
    }

    protected override string GetFilePath(Supplier item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    /// <summary>
    /// Searches suppliers by business name, up to <paramref name="maxResults"/> matches.
    /// </summary>
    public virtual List<Supplier> Search(string? query, int maxResults = 20)
    {
        EnsureLoaded();
        var all = GetAll();
        if (string.IsNullOrWhiteSpace(query))
            return all.Take(maxResults).ToList();

        return all
            .Where(s => s.ContactInfo != null && !string.IsNullOrEmpty(s.ContactInfo.BusinessName) &&
                        s.ContactInfo.BusinessName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
    }
}
