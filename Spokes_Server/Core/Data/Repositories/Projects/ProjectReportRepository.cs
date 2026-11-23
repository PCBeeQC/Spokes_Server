using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class ProjectReportRepository : JsonRepository<ProjectReport>
{
    private readonly SequenceService _sequenceService;

    public ProjectReportRepository(
        DiskPersistenceService writer,
        IConfiguration config,
        SequenceService sequenceService,
        ILogger<ProjectReportRepository> logger)
        : base(writer, config["DataPath"] ?? "", "*.json", logger)
    {
        _sequenceService = sequenceService;
    }

    public List<ProjectReport> GetByProject(string projectId)
    {
        EnsureLoaded();
        return _cache.Values
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.AsOfDate)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();
    }

    public string GenerateReportNumber(string prefix) =>
        _sequenceService.GenerateNumber("ProjectReport", string.IsNullOrWhiteSpace(prefix) ? "RPT" : prefix);

    protected override string GetFilePath(ProjectReport item) =>
        // /Data/Projects/{ProjId}/Reports/{ReportId}.json
        Path.Combine(_basePath, "Projects", item.ProjectId, "Reports", $"{item.Id}.json");

    public override void LoadFromDisk() =>
        LoadProjectSubfolderFromDisk("Reports", "project report");
}
