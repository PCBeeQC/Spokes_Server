using Spokes_Server.Core.Models.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class ProjectDocumentRepository : JsonRepository<ProjectDocument>
{
    private readonly string _rootDataPath;
    private readonly ILogger<ProjectDocumentRepository> _logger;

    public ProjectDocumentRepository(DiskPersistenceService writer, IConfiguration config, ILogger<ProjectDocumentRepository> logger)
        : base(writer, config["DataPath"] ?? "")
    {
        _rootDataPath = config["DataPath"] ?? "";
        _logger = logger;
    }

    public List<ProjectDocument> GetByProject(string projectId)
    {
        return _cache.Values
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.CreatedAt)
            .ToList();
    }

    public List<ProjectDocument> GetBusinessDocuments()
    {
        return _cache.Values
            .Where(d => string.IsNullOrEmpty(d.ProjectId))
            .OrderByDescending(d => d.CreatedAt)
            .ToList();
    }

    protected override string GetFilePath(ProjectDocument item)
    {
        if (string.IsNullOrEmpty(item.ProjectId))
        {
            return Path.Combine(_rootDataPath, "BusinessDocuments", $"{item.Id}.json");
        }
        return Path.Combine(_rootDataPath, "Projects", item.ProjectId, "Documents", $"{item.Id}.json");
    }

    public override void LoadFromDisk()
    {
        var projectsPath = Path.Combine(_rootDataPath, "Projects");
        if (Directory.Exists(projectsPath))
        {
            var files = Directory.GetFiles(projectsPath, "*.json", SearchOption.AllDirectories)
                                 .Where(f => f.Contains("Documents" + Path.DirectorySeparatorChar));
            LoadFiles(files);
        }

        var businessDocsPath = Path.Combine(_rootDataPath, "BusinessDocuments");
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
                var item = System.Text.Json.JsonSerializer.Deserialize<ProjectDocument>(json);
                if (item != null) _cache[item.Id] = item;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading project document {File}", file);
            }
        }
    }
}
