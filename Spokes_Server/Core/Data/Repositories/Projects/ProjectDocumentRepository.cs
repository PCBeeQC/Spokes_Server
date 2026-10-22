using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class ProjectDocumentRepository : JsonRepository<ProjectDocument>
{
    public ProjectDocumentRepository(DiskPersistenceService writer, IConfiguration config, ILogger<ProjectDocumentRepository> logger)
        : base(writer, config["DataPath"] ?? "", "*.json", logger)
    {
    }

    public List<ProjectDocument> GetByProject(string projectId)
    {
        EnsureLoaded();
        return _cache.Values
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.CreatedAt)
            .ToList();
    }

    public List<ProjectDocument> GetBusinessDocuments()
    {
        EnsureLoaded();
        return _cache.Values
            .Where(d => string.IsNullOrEmpty(d.ProjectId))
            .OrderByDescending(d => d.CreatedAt)
            .ToList();
    }

    protected override string GetFilePath(ProjectDocument item) =>
        string.IsNullOrEmpty(item.ProjectId)
            ? Path.Combine(_basePath, "BusinessDocuments", $"{item.Id}.json")
            : Path.Combine(_basePath, "Projects", item.ProjectId, "Documents", $"{item.Id}.json");

    public override void LoadFromDisk()
    {
        lock (_loadLock)
        {
            var tempDocs = new List<ProjectDocument>();

            var projectsPath = Path.Combine(_basePath, "Projects");
            if (Directory.Exists(projectsPath))
            {
                var files = Directory.GetFiles(projectsPath, "*.json", SearchOption.AllDirectories)
                                     .Where(f => f.Contains("Documents" + Path.DirectorySeparatorChar));
                LoadFiles(files, tempDocs);
            }

            var businessDocsPath = Path.Combine(_basePath, "BusinessDocuments");
            if (Directory.Exists(businessDocsPath))
            {
                var files = Directory.GetFiles(businessDocsPath, "*.json", SearchOption.TopDirectoryOnly);
                LoadFiles(files, tempDocs);
            }

            RawCache.Clear();
            foreach (var doc in tempDocs)
            {
                RawCache[doc.Id] = doc;
            }
            _isLoaded = true;
        }
    }

    private void LoadFiles(IEnumerable<string> files, List<ProjectDocument> destination)
    {
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var item = JsonSerializer.Deserialize<ProjectDocument>(json);
                if (item != null) destination.Add(item);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error loading project document {File}", file);
            }
        }
    }
}
