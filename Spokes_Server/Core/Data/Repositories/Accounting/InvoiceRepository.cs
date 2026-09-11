using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Accounting;

public class InvoiceRepository : JsonRepository<Invoice>
{
    private readonly SequenceService _sequenceService;

    public InvoiceRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService)
        // Invoices are stored per project or global? 
        // Let's store them globally for easier "Accounts Receivable" reporting later.
        : base(writer, Path.Combine(config["DataPath"] ?? "", "Invoices"), "*.json")
    {
        _sequenceService = sequenceService;
    }

    protected override string GetFilePath(Invoice item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    public string GenerateInvoiceNumber(string prefix)
    {
        return _sequenceService.GenerateNumber("Invoice", prefix);
    }

    public List<Invoice> GetByProject(string projectId)
    {
        return _cache.Values
            .Where(i => i.ProjectId == projectId)
            .OrderByDescending(i => i.Date)
            .ToList();
    }

    public List<Invoice> GetByProjectGroup(string projectGroupId)
    {
        return _cache.Values
            .Where(i => i.ProjectGroupId == projectGroupId)
            .OrderByDescending(i => i.Date)
            .ToList();
    }
}


