using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class QuoteRepository : JsonRepository<Quote>
{
    private readonly SequenceService _sequenceService;

    public QuoteRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService, ILogger<QuoteRepository> logger)
        // Base path is generic here, we calculate specific path per item
        : base(writer, config["DataPath"] ?? "", "*.json", logger)
    {
        _sequenceService = sequenceService;
    }

    public List<Quote> GetByProject(string projectId)
    {
        EnsureLoaded();
        return _cache.Values
            .Where(q => q.ProjectId == projectId)
            .OrderByDescending(q => q.Date)
            .ToList();
    }

    public string GenerateQuoteNumber(string prefix) =>
        _sequenceService.GenerateNumber("Quote", prefix);

    protected override string GetFilePath(Quote item) =>
        // /Data/Projects/{ProjId}/Quotes/{QuoteId}.json
        Path.Combine(_basePath, "Projects", item.ProjectId, "Quotes", $"{item.Id}.json");

    // Custom loader to scan Project folders for quotes
    public override void LoadFromDisk() =>
        LoadProjectSubfolderFromDisk("Quotes", "quote");
}
