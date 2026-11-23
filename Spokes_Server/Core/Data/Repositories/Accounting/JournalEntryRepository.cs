namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

public class JournalEntryRepository : JsonRepository<JournalEntry>
{
    private readonly SequenceService _sequenceService;

    public JournalEntryRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "JournalEntries"), "*.json")
    {
        _sequenceService = sequenceService;
    }

    protected override string GetFilePath(JournalEntry item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public string GenerateEntryNumber(string prefix = "JE") =>
        _sequenceService.GenerateNumber("JournalEntry", prefix);

    public JournalEntry? GetBySource(string sourceType, string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourceId))
            return null;

        return GetAll().FirstOrDefault(j =>
            string.Equals(j.SourceType, sourceType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(j.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
    }

    public List<JournalEntry> GetByDateRange(DateTime startDate, DateTime endDate)
    {
        var s = startDate.Date;
        var e = endDate.Date;
        return GetAll()
            .Where(j => j.Date.Date >= s && j.Date.Date <= e)
            .OrderBy(j => j.Date)
            .ThenBy(j => j.EntryNumber)
            .ToList();
    }

    public List<JournalEntry> GetByAccountCode(string glAccountCode, DateTime? startDate = null, DateTime? endDate = null)
    {
        if (string.IsNullOrWhiteSpace(glAccountCode)) return [];
        var code = glAccountCode.Trim();

        return GetAll()
            .Where(j => (!startDate.HasValue || j.Date.Date >= startDate.Value.Date) &&
                        (!endDate.HasValue || j.Date.Date <= endDate.Value.Date) &&
                        j.Lines.Any(l => string.Equals(l.GlAccountCode, code, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(j => j.Date)
            .ThenBy(j => j.EntryNumber)
            .ToList();
    }
}
