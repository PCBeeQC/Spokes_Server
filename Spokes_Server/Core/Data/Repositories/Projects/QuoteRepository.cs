using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class QuoteRepository : JsonRepository<Quote>
{
    private readonly SequenceService _sequenceService;
    private readonly ILogger<QuoteRepository> _logger;

    public QuoteRepository(DiskPersistenceService writer, IConfiguration config, SequenceService sequenceService, ILogger<QuoteRepository> logger)
        // Base path is generic here, we calculate specific path per item
        : base(writer, config["DataPath"] ?? "")
    {
        _sequenceService = sequenceService;
        _logger = logger;
    }

    public List<Quote> GetByProject(string projectId) =>
        _cache.Values
            .Where(q => q.ProjectId == projectId)
            .OrderByDescending(q => q.Date)
            .ToList();

    public string GenerateQuoteNumber(string prefix) =>
        _sequenceService.GenerateNumber("Quote", prefix);

    protected override string GetFilePath(Quote item) =>
        // /Data/Projects/{ProjId}/Quotes/{QuoteId}.json
        Path.Combine(_basePath, "Projects", item.ProjectId, "Quotes", $"{item.Id}.json");

    // Custom loader to scan Project folders for quotes
    public override void LoadFromDisk()
    {
        var projectsPath = Path.Combine(_basePath, "Projects");
        if (!Directory.Exists(projectsPath)) return;

        // Find all "Quotes" folders inside any project
        var files = Directory.GetFiles(projectsPath, "*.json", SearchOption.AllDirectories)
                             .Where(f => f.Contains("Quotes" + Path.DirectorySeparatorChar));

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var item = JsonSerializer.Deserialize<Quote>(json);
                if (item != null) _cache[item.Id] = item;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading quote {File}", file);
            }
        }
    }
}
