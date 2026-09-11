using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Communication;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Controllers;

[Route("internal/attachments")]
[ApiController]
[Authorize]
public class AttachmentController : SpokesControllerBase
{
    // Extensions that should be forced to download instead of inline display (XSS prevention)
    private static readonly HashSet<string> ForceDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".svg", ".xml", ".xhtml"
    };

    private readonly IConfiguration _configuration;
    private readonly ChatChannelRepository _channels;
    private readonly IEnumerable<IFileAccessProvider> _providers;
    private readonly UserService _userService;

    public AttachmentController(
        IConfiguration configuration,
        ChatChannelRepository channels,
        IEnumerable<IFileAccessProvider> providers,
        UserService userService)
    {
        _configuration = configuration;
        _channels = channels;
        _providers = providers;
        _userService = userService;
    }

    [HttpGet("thumb/{channelId}/{fileName}")]
    public Task<IActionResult> GetAttachmentThumb(string channelId, string fileName)
    {
        return GetAttachmentInternal(channelId, fileName, thumb: true);
    }

    [HttpGet("{channelId}/{fileName}")]
    public Task<IActionResult> GetAttachment(string channelId, string fileName)
    {
        return GetAttachmentInternal(channelId, fileName, thumb: false);
    }

    private async Task<IActionResult> GetAttachmentInternal(string channelId, string fileName, bool thumb)
    {
        // Prevent path traversal
        if (string.IsNullOrWhiteSpace(channelId) || channelId.Contains("..") || channelId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return BadRequest(Spokes_Server.Core.Utilities.SpokesResult.Failure("Invalid path parameters"));
        }

        // 1. Validate User Access to Channel
        var user = _userService.GetEmployee(User);
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return Unauthorized();
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            return NotFound(Spokes_Server.Core.Utilities.SpokesResult.Failure("Channel not found"));
        }

        var provider = _providers.FirstOrDefault(p => p.Category.Equals("chat", StringComparison.OrdinalIgnoreCase));
        if (provider != null)
        {
            bool canAccess = await provider.CanAccessAsync(user, channelId);
            if (!canAccess) return Forbid();
        }
        else
        {
            // Check participation fallback
            if (channel.ChannelType != ChatChannelType.General && !channel.ParticipantIds.Contains(user.Id))
            {
                return Forbid();
            }
        }

        // 2. Locate File
        var dataPath = _configuration["DataPath"] ?? "Data";
        var targetFileName = thumb ? $"{fileName}_thumb.jpg" : fileName;
        var filePath = Path.Combine(dataPath, "Attachments", channelId, targetFileName);

        if (thumb && !System.IO.File.Exists(filePath))
        {
            targetFileName = fileName;
            filePath = Path.Combine(dataPath, "Attachments", channelId, targetFileName);
        }

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        // 3. Serve File
        var contentType = GetContentType(targetFileName);
        var extension = Path.GetExtension(targetFileName);
        
        var safeInlineContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp", "application/pdf"
        };
        
        bool forceDownload = Request.Query.ContainsKey("download") || 
                             ForceDownloadExtensions.Contains(extension) || 
                             !safeInlineContentTypes.Contains(contentType);

        if (forceDownload)
        {
            return PhysicalFile(filePath, contentType, targetFileName, enableRangeProcessing: true);
        }

        var safeFileName = new string(targetFileName.Select(c => c > 127 ? '_' : c).ToArray());
        var encodedFileName = Uri.EscapeDataString(targetFileName);
        Response.Headers.ContentDisposition = $"inline; filename=\"{safeFileName}\"; filename*=UTF-8''{encodedFileName}";
        return PhysicalFile(filePath, contentType, enableRangeProcessing: true);
    }

    private string GetContentType(string path)
    {
        var types = GetMimeTypes();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return types.ContainsKey(ext) ? types[ext] : "application/octet-stream";
    }

    private Dictionary<string, string> GetMimeTypes()
    {
        return new Dictionary<string, string>
        {
            {".txt", "text/plain"},
            {".pdf", "application/pdf"},
            {".doc", "application/vnd.ms-word"},
            {".docx", "application/vnd.ms-word"},
            {".xls", "application/vnd.ms-excel"},
            {".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"},
            {".png", "image/png"},
            {".jpg", "image/jpeg"},
            {".jpeg", "image/jpeg"},
            {".gif", "image/gif"},
            {".webp", "image/webp"},
            {".csv", "text/csv"},
            {".bmp", "image/bmp"},
            {".mp4", "video/mp4"},
            {".webm", "video/webm"}
        };
    }

    [HttpGet("email/{messageId}/{attachmentId}")]
    public async Task<IActionResult> GetEmailAttachment(string messageId, string attachmentId,
        [FromQuery] string employeeId, [FromQuery] string? ct, [FromQuery] string? fn)
    {
        // Validate authenticated user
        var user = _userService.GetEmployee(User);
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return Unauthorized();
        }

        if (string.IsNullOrEmpty(employeeId))
        {
            return BadRequest(Spokes_Server.Core.Utilities.SpokesResult.Failure("employeeId is required"));
        }

        var requestingEmployeeId = user.Id;
        var isAdmin = user.IsAdmin;

        // Vulnerability Patch: Enforce ownership
        if (!isAdmin && requestingEmployeeId != employeeId)
        {
            return Forbid();
        }

        // Prevent path traversal
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Contains("..") || messageId.Contains("/") || messageId.Contains("\\") ||
            string.IsNullOrWhiteSpace(attachmentId) || attachmentId.Contains("..") || attachmentId.Contains("/") || attachmentId.Contains("\\") ||
            employeeId.Contains("..") || employeeId.Contains("/") || employeeId.Contains("\\"))
        {
            return BadRequest(Spokes_Server.Core.Utilities.SpokesResult.Failure("Invalid path parameters"));
        }

        var dataPath = _configuration["DataPath"] ?? "Data";
        var baseDirPath = Path.GetFullPath(Path.Combine(dataPath, "Employees", employeeId, "Email", "Attachments", messageId));
        var filePath = Path.GetFullPath(Path.Combine(baseDirPath, attachmentId));

        if (!filePath.StartsWith(baseDirPath) || !System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        // Use content type from query param (passed from EmailAttachmentMeta.ContentType)
        // Fall back to extension-based detection, then octet-stream
        var contentType = !string.IsNullOrEmpty(ct) ? ct : GetContentType(filePath);
        var originalFileName = !string.IsNullOrEmpty(fn) ? fn : attachmentId;

        var extension = Path.GetExtension(originalFileName);
        
        var safeInlineContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp", "application/pdf"
        };
        
        bool forceDownload = Request.Query.ContainsKey("download") || 
                             ForceDownloadExtensions.Contains(extension) || 
                             !safeInlineContentTypes.Contains(contentType);

        // Support download mode or force download for dangerous types
        if (forceDownload)
        {
            return PhysicalFile(filePath, contentType, originalFileName, enableRangeProcessing: true);
        }

        // Serve inline — set Content-Disposition: inline to prevent browser auto-download
        // Use ASCII-safe filename + RFC 5987 UTF-8 encoding for non-ASCII characters
        var safeFileName = new string(originalFileName.Select(c => c > 127 ? '_' : c).ToArray());
        var encodedFileName = Uri.EscapeDataString(originalFileName);
        Response.Headers.ContentDisposition = $"inline; filename=\"{safeFileName}\"; filename*=UTF-8''{encodedFileName}";
        return PhysicalFile(filePath, contentType, enableRangeProcessing: true);
    }
}




