namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

public class TaxFilingPeriodRepository : JsonRepository<TaxFilingPeriod>
{
    public TaxFilingPeriodRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "TaxFilingPeriods"), "*.json")
    {
    }

    protected override string GetFilePath(TaxFilingPeriod item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public TaxFilingPeriod? GetByDates(DateTime startDate, DateTime endDate) =>
        GetAll().FirstOrDefault(p =>
            p.StartDate.Date == startDate.Date &&
            p.EndDate.Date == endDate.Date);
}
