using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class InvoiceRepository : JsonRepository<Invoice>
{
    private readonly SequenceService _sequenceService;

    public InvoiceRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService)
        : base(writer, Path.Combine(config["DataPath"] ?? "", "Invoices"), "*.json")
    {
        _sequenceService = sequenceService;
    }

    protected override string GetFilePath(Invoice item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public string GenerateInvoiceNumber(string prefix) =>
        _sequenceService.GenerateNumber("Invoice", prefix);

    public List<Invoice> GetByProject(string projectId) =>
        _cache.Values
            .Where(i => i.ProjectId == projectId)
            .OrderByDescending(i => i.Date)
            .ToList();

    public List<Invoice> GetByProjectGroup(string projectGroupId) =>
        _cache.Values
            .Where(i => i.ProjectGroupId == projectGroupId)
            .OrderByDescending(i => i.Date)
            .ToList();
}


