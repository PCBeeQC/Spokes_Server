using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Components.Forms;

namespace Spokes_Server.Core.Services.Core;

public interface IFileService
{
    Task<string> UploadAsync(string category, string contextId, IBrowserFile file);
    Task<string> UploadStreamAsync(string category, string contextId, Stream stream, string fileName, string? encryptionKey = null, string? predefinedSafeName = null);
    string GenerateSafeName(string originalFileName);
    Task DeleteAsync(string relativeUrl);
    string GetPhysicalPath(string category, string contextId, string fileName);
    void CleanTempContext(string contextId);
    void PurgeStaleTempFiles(TimeSpan? maxAge = null);
}

public class FileService : IFileService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileService> _logger;
    private readonly ICryptoService _crypto;
    private readonly string _basePath;

    public FileService(IConfiguration configuration, ILogger<FileService> logger, ICryptoService crypto)
    {
        _configuration = configuration;
        _logger = logger;
        _crypto = crypto;

        // Ensure base path exists
        _basePath = Path.Combine(_configuration["DataPath"] ?? "Data", "Uploads");
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }

        // Purge any stale abandoned temporary files on startup
        Task.Run(() => PurgeStaleTempFiles());
    }

    public async Task<string> UploadAsync(string category, string contextId, IBrowserFile file)
    {
        try
        {
            // Limit stream copy size (handled by caller max size, but we enforce here too potentially)
            // Wrapper for stream upload
            using var stream = file.OpenReadStream(maxAllowedSize: 50 * 1024 * 1024);
            return await UploadStreamAsync(category, contextId, stream, file.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file {FileName} to {Category}/{ContextId}", file.Name, category, contextId);
            throw; // Propagate to UI
        }
    }

    public string GenerateSafeName(string originalFileName)
    {
        var sanitizedFileName = Path.GetFileName(originalFileName);
        var extension = Path.GetExtension(sanitizedFileName);
        return $"{DateTime.Now.Ticks}_{Guid.NewGuid().ToString().Substring(0, 8)}{extension}";
    }

    public async Task<string> UploadStreamAsync(string category, string contextId, Stream stream, string fileName, string? encryptionKey = null, string? predefinedSafeName = null)
    {
        try
        {
            // Stricter validation - reject path traversal attempts
            if (string.IsNullOrWhiteSpace(category) ||
                category.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                category.Contains(".."))
                throw new ArgumentException("Invalid category");

            if (string.IsNullOrWhiteSpace(contextId) ||
                contextId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                contextId.Contains(".."))
                throw new ArgumentException("Invalid context ID");

            var folder = Path.Combine(_basePath, category, contextId);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var safeName = string.IsNullOrWhiteSpace(predefinedSafeName) 
                ? GenerateSafeName(fileName) 
                : predefinedSafeName;

            var fullPath = Path.Combine(folder, safeName);

            using var fileStream = new FileStream(fullPath, FileMode.Create);

            if (!string.IsNullOrEmpty(encryptionKey))
            {
                await _crypto.EncryptStreamAsync(stream, fileStream, encryptionKey);
            }
            else
            {
                await stream.CopyToAsync(fileStream);
            }

            _logger.LogInformation("File uploaded to {Path}", fullPath);

            // Return API URL
            return $"/spokesapi/files/{category}/{contextId}/{safeName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file {FileName} to {Category}/{ContextId}", fileName, category, contextId);
            throw;
        }
    }

    public async Task DeleteAsync(string relativeUrl)
    {
        if (string.IsNullOrEmpty(relativeUrl)) return;

        // URL format: /spokesapi/files/{category}/{contextId}/{fileName}
        if (!relativeUrl.StartsWith("/spokesapi/files/")) return;

        try
        {
            // Extract parts
            var parts = relativeUrl.Substring("/spokesapi/files/".Length).Split('/');
            if (parts.Length != 3) return;

            var category = parts[0];
            var contextId = parts[1];
            var fileName = parts[2];

            var path = GetPhysicalPath(category, contextId, fileName);
            if (File.Exists(path))
            {
                await Task.Run(() => File.Delete(path));
                _logger.LogInformation("File deleted: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file {Url}", relativeUrl);
        }
    }

    public string GetPhysicalPath(string category, string contextId, string fileName)
    {
        // Sanitize inputs
        category = Path.GetFileName(category);
        contextId = Path.GetFileName(contextId);
        fileName = Path.GetFileName(fileName);

        return Path.Combine(_basePath, category, contextId, fileName);
    }

    public void CleanTempContext(string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId) ||
            contextId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            contextId.Contains(".."))
        {
            return;
        }

        try
        {
            var folder = Path.Combine(_basePath, "temp", Path.GetFileName(contextId));
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
                _logger.LogInformation("Cleaned temp context directory: {Folder}", folder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean temp context directory for contextId {ContextId}", contextId);
        }
    }

    public void PurgeStaleTempFiles(TimeSpan? maxAge = null)
    {
        try
        {
            var tempFolder = Path.Combine(_basePath, "temp");
            if (!Directory.Exists(tempFolder)) return;

            var threshold = DateTime.UtcNow - (maxAge ?? TimeSpan.FromHours(1));
            var dirs = Directory.GetDirectories(tempFolder);
            foreach (var dir in dirs)
            {
                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.LastWriteTimeUtc < threshold)
                    {
                        dirInfo.Delete(recursive: true);
                        _logger.LogInformation("Purged stale temp upload directory: {Dir}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to purge stale temp directory {Dir}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning for stale temp files");
        }
    }
}

