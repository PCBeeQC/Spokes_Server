using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class ProjectDocumentRepository : JsonRepository<ProjectDocument>
{
    private readonly ILogger<ProjectDocumentRepository> _logger;

    public ProjectDocumentRepository(DiskPersistenceService writer, IConfiguration config, ILogger<ProjectDocumentRepository> logger)
        : base(writer, config["DataPath"] ?? "")
    {
        _logger = logger;
    }

    public List<ProjectDocument> GetByProject(string projectId) =>
        _cache.Values
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.CreatedAt)
            .ToList();

    public List<ProjectDocument> GetBusinessDocuments() =>
        _cache.Values
            .Where(d => string.IsNullOrEmpty(d.ProjectId))
            .OrderByDescending(d => d.CreatedAt)
            .ToList();

    protected override string GetFilePath(ProjectDocument item) =>
        string.IsNullOrEmpty(item.ProjectId)
            ? Path.Combine(_basePath, "BusinessDocuments", $"{item.Id}.json")
            : Path.Combine(_basePath, "Projects", item.ProjectId, "Documents", $"{item.Id}.json");

    public override void LoadFromDisk()
    {
        var projectsPath = Path.Combine(_basePath, "Projects");
        if (Directory.Exists(projectsPath))
        {
            var files = Directory.GetFiles(projectsPath, "*.json", SearchOption.AllDirectories)
                                 .Where(f => f.Contains("Documents" + Path.DirectorySeparatorChar));
            LoadFiles(files);
        }

        var businessDocsPath = Path.Combine(_basePath, "BusinessDocuments");
        if (Directory.Exists(businessDocsPath))
        {
            var files = Directory.GetFiles(businessDocsPath, "*.json", SearchOption.TopDirectoryOnly);
            LoadFiles(files);
        }
    }

    private void LoadFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var item = JsonSerializer.Deserialize<ProjectDocument>(json);
                if (item != null) _cache[item.Id] = item;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading project document {File}", file);
            }
        }
    }
}
