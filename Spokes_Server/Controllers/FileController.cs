using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Controllers;

[Route("spokesapi/files")]
[ApiController]
[Authorize]
public class FileController : SpokesControllerBase
{
    private readonly IFileService _fileService;
    private readonly IFileAuthorizationService _fileAuthService;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ChatChannelRepository _channels;
    private readonly ILogger<FileController> _logger;
    private readonly UserService _userService;
    private readonly ImageProcessingService _imageService;
    private readonly IConfiguration? _config;
    private readonly IStorageHealthService? _storageHealth;
    private readonly FileTokenService? _fileTokenService;
    private readonly IMp4FastStartService? _fastStartService;

    [ActivatorUtilitiesConstructor]
    public FileController(
        IFileService fileService,
        IFileAuthorizationService fileAuthService,
        CoreContext coreContext,
        CommunicationContext communicationContext,
        ILogger<FileController> logger,
        UserService userService,
        ImageProcessingService imageService,
        IStorageHealthService? storageHealth = null,
        FileTokenService? fileTokenService = null,
        IConfiguration? config = null,
        IMp4FastStartService? fastStartService = null)
        : this(
            fileService,
            fileAuthService,
            coreContext.CompanyProfile,
            logger,
            userService,
            imageService,
            communicationContext.ChatChannels,
            storageHealth,
            fileTokenService,
            config,
            fastStartService)
    {
    }

    internal FileController(
        IFileService fileService,
        IFileAuthorizationService fileAuthService,
        CompanyProfileRepository companyProfiles,
        ILogger<FileController> logger,
        UserService userService,
        ImageProcessingService imageService,
        ChatChannelRepository channels,
        IStorageHealthService? storageHealth = null,
        FileTokenService? fileTokenService = null,
        IConfiguration? config = null,
        IMp4FastStartService? fastStartService = null)
    {
        _fileService = fileService;
        _fileAuthService = fileAuthService;
        _companyProfiles = companyProfiles;
        _logger = logger;
        _userService = userService;
        _imageService = imageService;
        _channels = channels;
        _storageHealth = storageHealth;
        _fileTokenService = fileTokenService;
        _config = config;
        _fastStartService = fastStartService;
    }

    // Extensions that should be forced to download instead of inline display (XSS prevention)
    private static readonly HashSet<string> ForceDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".svg", ".xml", ".xhtml"
    };

    private static readonly HashSet<string> SafeInlineContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp", "application/pdf"
    };

    private static readonly HashSet<string> SafeThumbnailFallbackExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _thumbRegenLocks = new(StringComparer.OrdinalIgnoreCase);

    private record UploadContext(
        Employee User,
        string? EncryptionKey);

    private IActionResult? ValidateUploadPrelude(
        string category,
        string contextId,
        string? token,
        out UploadContext? context,
        bool isThumbnail = false)
    {
        context = null;

        if (!IsValidPathSegment(category) || !IsValidPathSegment(contextId))
        {
            return BadRequestError("Invalid path parameters");
        }

        var config = _config;
        if (config?.GetValue<bool>("Spokes_DemoMode") == true)
        {
            var demoUser = _userService.GetEmployee(User);
            if (demoUser == null || !demoUser.IsAdmin)
            {
                return StatusCodeError(403, "Uploads are disabled in demo mode.");
            }
        }

        if (_storageHealth != null && !_storageHealth.IsUploadAllowed)
        {
            return BadRequestError("Server storage is critically full (< 100 MB remaining). Uploads are temporarily paused to protect database integrity. Please free disk space on the server.");
        }

        var user = _userService.GetEmployee(User);
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return UnauthorizedError("Authentication required.");
        }

        var sessionId = User.FindFirst("SessionId")?.Value;

        var tokenValidation = _fileTokenService?.ValidateAccessToken(category, contextId, "*", token, user.Id, sessionId)
            ?? (string.IsNullOrEmpty(token) ? FileTokenValidationResult.None() : FileTokenValidationResult.Invalid());
        if (!tokenValidation.IsSuccess)
        {
            return StatusCodeError(410, "Upload Token Expired or Invalid");
        }
        string? tokenEncryptionKey = tokenValidation.EncryptionKey;

        if (!_fileAuthService.IsKnownCategory(category))
        {
            return BadRequestError($"Unknown file category: {category}");
        }

        if (!_fileAuthService.CanUpload(category, contextId, user))
        {
            return ForbiddenError("Access forbidden.", "FORBIDDEN");
        }

        var keyResolution = _fileAuthService.ResolveUploadKey(new FileUploadKeyRequest(category, contextId, user, tokenEncryptionKey, isThumbnail, HttpContext));
        if (!keyResolution.IsSuccess)
        {
            return BadRequestError(keyResolution.ErrorMessage ?? "Encryption key resolution failed.");
        }
        string? encryptionKey = keyResolution.EncryptionKey;

        context = new UploadContext(user, encryptionKey);
        return null;
    }

    [HttpPost("{category}/{contextId}")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> UploadFile(string category, string contextId, [FromForm] IFormFileCollection files, [FromQuery] string? t = null)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequestError("No files uploaded");
        }

        var errorResult = ValidateUploadPrelude(category, contextId, t, out var uploadCtx);
        if (errorResult != null) return errorResult;

        var user = uploadCtx!.User;
        var encryptionKey = uploadCtx.EncryptionKey;

        var profile = _companyProfiles.Get();
        long maxFileSize = profile?.MaxFileUploadSizeBytes ?? 268435456;

        bool isMobileUpload = Request.Headers.TryGetValue("X-Upload-Platform", out var platformVal)
            && string.Equals(platformVal, "mobile", StringComparison.OrdinalIgnoreCase);

        int serverMaxDim;
        int serverQuality;

        if (isMobileUpload)
        {
            // Mobile device already applied configured mobile compression settings client-side.
            // Bypass server-side downscaling (preserve uploaded bytes, generate thumbnail).
            serverMaxDim = 0;
            serverQuality = 90;
        }
        else if (category.Equals("albums", StringComparison.OrdinalIgnoreCase))
        {
            serverMaxDim = profile?.Albums?.DesktopPhotoMaxDimension ?? 3840;
            serverQuality = profile?.Albums?.DesktopPhotoQuality ?? 90;
        }
        else if (category.Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            serverMaxDim = profile?.Chat?.DesktopPhotoMaxDimension ?? 3840;
            serverQuality = profile?.Chat?.DesktopPhotoQuality ?? 85;
        }
        else
        {
            serverMaxDim = 3840;
            serverQuality = 90;
        }

        foreach (var file in files)
        {
            if (file.Length > maxFileSize)
            {
                return BadRequestError($"File {file.FileName} exceeds the max size of {maxFileSize} bytes.");
            }
        }

        var results = new FileUploadResult[files.Count];
        Exception? processingException = null;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4)
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            parallelOptions,
            async (index, ct) =>
            {
                var file = files[index];
                try
                {
                    int? imageWidth = null;
                    int? imageHeight = null;
                    double? durationSeconds = null;
                    bool hasThumbnail = false;
                    string safeName = _fileService.GenerateSafeName(file.FileName);
                    Stream uploadStream = file.OpenReadStream();

                    try
                    {
                        // Attempt to process image and generate thumbnail (skip for temp files to avoid unnecessary processing and orphaned thumbnails)
                        if (!category.Equals("temp", StringComparison.OrdinalIgnoreCase) &&
                            _imageService.TryProcessUploadImage(uploadStream, file.ContentType, file.Length, out var processedImage, serverMaxDim, serverQuality))
                        {
                            if (processedImage.ProcessedStream != uploadStream)
                            {
                                uploadStream.Dispose();
                                uploadStream = processedImage.ProcessedStream;
                            }

                            imageWidth = processedImage.Width;
                            imageHeight = processedImage.Height;

                            if (processedImage.ThumbnailBytes != null)
                            {
                                using var msThumb = new MemoryStream(processedImage.ThumbnailBytes);
                                await _fileService.UploadThumbnailAsync(category, contextId, msThumb, safeName, encryptionKey);
                                hasThumbnail = true;
                            }
                        }
                        else if (!category.Equals("temp", StringComparison.OrdinalIgnoreCase) &&
                                 _fastStartService != null &&
                                 (file.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || IsMp4Video(file.FileName)))
                        {
                            if (_fastStartService.TryProcessMp4(uploadStream, out var videoMeta) && videoMeta.Success)
                            {
                                if (videoMeta.WasModified && videoMeta.ProcessedStream != null)
                                {
                                    uploadStream.Dispose();
                                    uploadStream = videoMeta.ProcessedStream;
                                }

                                imageWidth = videoMeta.Width;
                                imageHeight = videoMeta.Height;
                                durationSeconds = videoMeta.DurationSeconds;
                            }
                        }

                        if (uploadStream.CanSeek)
                        {
                            uploadStream.Position = 0;
                        }
                        // Pass to IFileService which sanitizes and saves with optional encryption
                        var url = await _fileService.UploadStreamAsync(category, contextId, uploadStream, file.FileName, encryptionKey, safeName);

                        results[index] = new FileUploadResult(
                            url,
                            file.FileName,
                            file.ContentType,
                            uploadStream.CanSeek ? uploadStream.Length : file.Length,
                            imageWidth,
                            imageHeight,
                            hasThumbnail,
                            durationSeconds);
                    }
                    finally
                    {
                        uploadStream?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref processingException, ex, null);
                }
            });

        if (processingException != null)
        {
            return StatusCodeError(500, $"Failed to process file: {processingException.Message}", "UPLOAD_PROCESSING_FAILED");
        }

        return Ok(results.ToList());
    }

    [HttpPost("thumbnail/{category}/{contextId}/{fileName}")]
    public async Task<IActionResult> UploadThumbnail(string category, string contextId, string fileName, [FromForm] IFormFile file, [FromQuery] string? t = null)
    {
        fileName = Path.GetFileName(fileName);
        
        if (!IsValidPathSegment(fileName))
        {
            return BadRequestError("Invalid path parameters");
        }

        if (category.Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequestError("Thumbnails are not supported for temporary files.");
        }

        if (file == null || file.Length == 0) return BadRequestError("No file uploaded");
        if (file.Length > 5 * 1024 * 1024) return BadRequestError("Thumbnail must be smaller than 5MB");

        var errorResult = ValidateUploadPrelude(category, contextId, t, out var uploadCtx, isThumbnail: true);
        if (errorResult != null) return errorResult;

        var encryptionKey = uploadCtx!.EncryptionKey;

        var thumbFileName = $"{fileName}_thumb.jpg";
        var folder = _fileService.GetPhysicalPath(category, contextId, "");
        var fullPath = !string.IsNullOrEmpty(folder) 
            ? Path.Combine(folder, thumbFileName) 
            : _fileService.GetPhysicalPath(category, contextId, thumbFileName);

        if ((!string.IsNullOrEmpty(fullPath) && System.IO.File.Exists(fullPath)) || _fileService.FileExists(category, contextId, thumbFileName))
        {
            return BadRequestError("Thumbnail already exists for this file. Overwriting is not permitted.");
        }

        using var stream = file.OpenReadStream();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        try
        {
            if (!_imageService.TryGenerateThumbnail(memoryStream, out var thumbData) || thumbData == null)
            {
                return BadRequestError("Invalid image format or dimensions too large.");
            }

            using (thumbData)
            {
                await _fileService.UploadThumbnailAsync(category, contextId, thumbData, fileName, encryptionKey);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Disk or storage I/O error uploading thumbnail for {FileName} in category {Category}", fileName, category);
            try { await _fileService.DeleteFileAsync(category, contextId, thumbFileName); } catch (Exception cleanupEx) { _logger.LogDebug(cleanupEx, "Failed to clean up file at {Path}", thumbFileName); }
            return StatusCodeError(500, "Disk or storage I/O error occurred while saving thumbnail.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Permission denied accessing storage while uploading thumbnail for {FileName} in category {Category}", fileName, category);
            try { await _fileService.DeleteFileAsync(category, contextId, thumbFileName); } catch (Exception cleanupEx) { _logger.LogDebug(cleanupEx, "Failed to clean up file at {Path}", thumbFileName); }
            return StatusCodeError(500, "Permission denied accessing storage while saving thumbnail.");
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            _logger.LogWarning(ex, "Invalid image format or corrupt cryptographic data for thumbnail of {FileName}", fileName);
            try { await _fileService.DeleteFileAsync(category, contextId, thumbFileName); } catch (Exception cleanupEx) { _logger.LogDebug(cleanupEx, "Failed to clean up file at {Path}", thumbFileName); }
            return BadRequestError("Invalid image format.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing thumbnail for file {FileName}", fileName);
            try { await _fileService.DeleteFileAsync(category, contextId, thumbFileName); } catch (Exception cleanupEx) { _logger.LogDebug(cleanupEx, "Failed to clean up file at {Path}", thumbFileName); }
            return StatusCodeError(500, "An error occurred while generating the thumbnail.");
        }

        return Ok();
    }

    [HttpGet("thumb/{category}/{contextId}/{fileName}")]
    public IActionResult GetFileThumb(string category, string contextId, string fileName, [FromQuery] string? t = null)
        => GetFileInternal(category, contextId, fileName, t, thumb: true);

    [HttpGet("{category}/{contextId}/{fileName}")]
    public IActionResult GetFile(string category, string contextId, string fileName, [FromQuery] string? t = null)
        => GetFileInternal(category, contextId, fileName, t, thumb: false);

    [HttpGet("/internal/attachments/{channelId}/{fileName}")]
    [HttpGet("/spokesapi/attachments/{channelId}/{fileName}")]
    public IActionResult GetLegacyAttachment(string channelId, string fileName, [FromQuery] string? t = null)
        => GetFileInternal("chat", channelId, fileName, t, thumb: false);

    [HttpGet("/internal/attachments/thumb/{channelId}/{fileName}")]
    [HttpGet("/spokesapi/attachments/thumb/{channelId}/{fileName}")]
    public IActionResult GetLegacyAttachmentThumb(string channelId, string fileName, [FromQuery] string? t = null)
        => GetFileInternal("chat", channelId, fileName, t, thumb: true);

    [HttpGet("demo-media/{fileName}")]
    public IActionResult GetDemoMedia(string fileName)
    {
        fileName = Path.GetFileName(fileName);
        var config = _config;
        if (config == null || (!config.GetValue<bool>("Spokes_DemoMode") && !config.GetValue<bool>("Spokes_DemoSetup")))
        {
            return NotFoundError("Demo media not available.", "DEMO_MEDIA_NOT_FOUND");
        }

        var dataPath = config["DataPath"] ?? "Data";
        var demoMediaPath = Path.Combine(dataPath, "DemoMedia", fileName);
        
        if (System.IO.File.Exists(demoMediaPath))
        {
            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(fileName, out var contentType))
            {
                contentType = "application/octet-stream";
            }
            return PhysicalFile(Path.GetFullPath(demoMediaPath), contentType);
        }
        return NotFoundError("Demo media file not found.", "NOT_FOUND");
    }

    [HttpGet("thumb/demo-media/{fileName}")]
    public IActionResult GetDemoMediaThumb(string fileName)
        => GetDemoMedia(fileName);

    [HttpGet("email/{messageId}/{attachmentId}")]
    [HttpGet("/internal/attachments/email/{messageId}/{attachmentId}")]
    [HttpGet("/spokesapi/attachments/email/{messageId}/{attachmentId}")]
    public IActionResult GetEmailAttachment(
        string messageId,
        string attachmentId,
        [FromQuery] string employeeId,
        [FromQuery] string? ct = null,
        [FromQuery] string? fn = null)
    {
        // Validate authenticated user
        var user = _userService.GetEmployee(User);
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        if (string.IsNullOrEmpty(employeeId))
        {
            return BadRequestError("employeeId is required");
        }

        // Enforce ownership via EmailFileAccessProvider
        if (!_fileAuthService.CanAccess("email", employeeId, user))
        {
            return ForbiddenError("Access to email attachment forbidden.", "FORBIDDEN");
        }


        // Prevent path traversal
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Contains("..") || messageId.Contains('/') || messageId.Contains('\\') ||
            string.IsNullOrWhiteSpace(attachmentId) || attachmentId.Contains("..") || attachmentId.Contains('/') || attachmentId.Contains('\\') ||
            employeeId.Contains("..") || employeeId.Contains('/') || employeeId.Contains('\\'))
        {
            return BadRequestError("Invalid path parameters");
        }

        string filePath;
        try
        {
            filePath = _fileService.GetEmailAttachmentPhysicalPath(employeeId, messageId, attachmentId);
        }
        catch (ArgumentException)
        {
            return BadRequestError("Invalid path parameters");
        }

        if (!System.IO.File.Exists(filePath))
        {
            try
            {
                var attachmentsDir = Path.GetDirectoryName(filePath);
                var emailDir = Directory.GetParent(attachmentsDir ?? "")?.FullName;
                if (!string.IsNullOrEmpty(emailDir))
                {
                    var mimePath = Path.Combine(emailDir, "Mime", $"{messageId}.eml");
                    if (System.IO.File.Exists(mimePath))
                    {
                        using var stream = System.IO.File.OpenRead(mimePath);
                        var mime = MimeKit.MimeMessage.Load(stream);
                        var part = mime.Attachments.OfType<MimeKit.MimePart>()
                            .FirstOrDefault(p => !string.IsNullOrEmpty(fn) && p.FileName == fn)
                            ?? mime.Attachments.OfType<MimeKit.MimePart>().FirstOrDefault();
                        if (part != null && part.Content != null)
                        {
                            Directory.CreateDirectory(attachmentsDir!);
                            using var outStream = System.IO.File.Create(filePath);
                            part.Content.DecodeTo(outStream);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract missing email attachment from MIME for message {MessageId}", messageId);
            }
        }

        if (!System.IO.File.Exists(filePath))
        {
            return NotFoundError("Attachment not found");
        }

        // Use content type from query param or resolve from IFileService
        var contentType = !string.IsNullOrEmpty(ct) ? ct : _fileService.GetContentType(filePath);
        var originalFileName = !string.IsNullOrEmpty(fn) ? fn : attachmentId;

        var extension = Path.GetExtension(originalFileName);

        bool forceDownload = Request.Query.ContainsKey("download") || 
                             ForceDownloadExtensions.Contains(extension) || 
                             !SafeInlineContentTypes.Contains(contentType);

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

    private IActionResult GetFileInternal(string category, string contextId, string fileName, string? t, bool thumb)
    {
        // 0. Validate route parameters - prevent path traversal
        if (!IsValidPathSegment(category) || !IsValidPathSegment(contextId) || !IsValidPathSegment(fileName))
        {
            return BadRequestError("Invalid path parameters");
        }

        fileName = Path.GetFileName(fileName);

        // 1. Identify User (always available from [Authorize] middleware)
        var user = _userService.GetEmployee(User);
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return UnauthorizedError("Authentication required.");

        var sessionId = User.FindFirst("SessionId")?.Value;

        // Extract decryption key from token (auth is handled by [Authorize])
        var tokenValidation = _fileTokenService?.ValidateAccessToken(category, contextId, fileName, t, user.Id, sessionId)
            ?? (string.IsNullOrEmpty(t) ? FileTokenValidationResult.None() : FileTokenValidationResult.Invalid());
        if (!tokenValidation.IsSuccess)
        {
            return StatusCodeError(410, "File Token Expired or Invalid");
        }
        string? tokenEncryptionKey = tokenValidation.EncryptionKey;

        return ServeFileForUser(category, contextId, fileName, user, thumb, tokenEncryptionKey);
    }

    /// <summary>
    /// Serves an authorized file for an authenticated user with permission checks and on-the-fly decryption.
    /// Can be called directly by user-scoped session bridges (e.g. slideshow presentations) to ensure
    /// the full FileController authorization and decryption pipeline is applied.
    /// </summary>
    public IActionResult ServeFileForUser(
        string category,
        string contextId,
        string fileName,
        Employee user,
        bool thumb,
        string? tokenEncryptionKey = null)
    {
        if (!IsValidPathSegment(category) || !IsValidPathSegment(contextId) || !IsValidPathSegment(fileName))
        {
            return BadRequestError("Invalid path parameters");
        }

        fileName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(fileName);

        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
        {
            return UnauthorizedError("Authentication required.");
        }

        // 2. Find Access Provider
        // Temporary staging files are for internal ingestion only and cannot be downloaded
        if (category.Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            return NotFoundError("Temporary files cannot be downloaded directly.", "NOT_FOUND");
        }

        // 3. Check Permissions
        if (category.Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            var channel = _channels.GetById(contextId);
            if (channel == null)
            {
                return NotFoundError("Channel not found");
            }

            if (_fileAuthService.IsKnownCategory("chat"))
            {
                if (!_fileAuthService.CanAccess(category, contextId, user))
                {
                    return ForbiddenError("Access forbidden.", "FORBIDDEN");
                }
            }
            else
            {
                if (channel.ChannelType != ChatChannelType.General && !channel.ParticipantIds.Contains(user.Id))
                {
                    return ForbiddenError("Access to channel attachment forbidden.", "FORBIDDEN");
                }
            }
        }
        else
        {
            // If no provider is defined for this category, deny access by default for security
            if (!_fileAuthService.IsKnownCategory(category))
            {
                return BadRequestError($"Unknown file category: {category}");
            }

            if (!_fileAuthService.CanAccess(category, contextId, user))
            {
                return ForbiddenError("Access forbidden.", "FORBIDDEN");
            }
        }

        // 4. Serve File Securely
        var targetFileName = thumb ? FileService.GetThumbnailFileName(fileName) : fileName;
        var path = _fileService.GetPhysicalPath(category, contextId, targetFileName);
        bool fellBackToOriginalForThumb = false;
        
        if (thumb && !System.IO.File.Exists(path))
        {
            if (category.Equals("chat", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var legacyPath = _fileService.GetAttachmentPhysicalPath(contextId, fileName, thumb: true);
                    if (System.IO.File.Exists(legacyPath))
                    {
                        path = legacyPath;
                        targetFileName = Path.GetFileName(legacyPath);
                        if (!targetFileName.EndsWith("_thumb.jpg", StringComparison.OrdinalIgnoreCase))
                        {
                            fellBackToOriginalForThumb = true;
                        }
                    }
                }
                catch (ArgumentException) { }
            }

            if (!System.IO.File.Exists(path))
            {
                // Only allow falling back to original file if the original file itself is a safe image format.
                // Videos, archives, audio, and documents must never stream as thumbnail responses.
                if (SafeThumbnailFallbackExtensions.Contains(extension))
                {
                    fellBackToOriginalForThumb = true;
                    targetFileName = fileName;
                    path = _fileService.GetPhysicalPath(category, contextId, targetFileName);

                    if (!System.IO.File.Exists(path) && category.Equals("chat", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var legacyPath = _fileService.GetAttachmentPhysicalPath(contextId, fileName, thumb: false);
                            if (System.IO.File.Exists(legacyPath))
                            {
                                path = legacyPath;
                                targetFileName = Path.GetFileName(legacyPath);
                            }
                        }
                        catch (ArgumentException) { }
                    }
                }
                else
                {
                    return NotFoundError("Thumbnail not found.", "NOT_FOUND");
                }
            }
        }
        else if (!thumb && !System.IO.File.Exists(path) && category.Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var legacyPath = _fileService.GetAttachmentPhysicalPath(contextId, fileName, thumb: false);
                if (System.IO.File.Exists(legacyPath))
                {
                    path = legacyPath;
                    targetFileName = Path.GetFileName(legacyPath);
                }
            }
            catch (ArgumentException) { }
        }

        if (!System.IO.File.Exists(path))
        {
            return NotFoundError("File not found.", "NOT_FOUND");
        }

        var httpContext = ControllerContext?.HttpContext;
        string? activeEncryptionKey = null;

        // 5. Resolve encryption key if needed
        if (category.Equals("chat", StringComparison.OrdinalIgnoreCase) || category.Equals("albums", StringComparison.OrdinalIgnoreCase))
        {
            var keyResult = _fileAuthService.ResolveDownloadKey(category, contextId, user, tokenEncryptionKey, httpContext);
            if (!keyResult.IsSuccess)
            {
                return BadRequestError(keyResult.ErrorMessage ?? "Access denied.");
            }

            if (keyResult.IsEncrypted && !string.IsNullOrEmpty(keyResult.EncryptionKey))
            {
                activeEncryptionKey = keyResult.EncryptionKey;
            }
        }

        // 5.5 Self-heal missing thumbnail on demand so subsequent requests serve the compact JPEG thumbnail directly
        byte[]? regeneratedThumbBytes = null;
        if (fellBackToOriginalForThumb &&
            TrySelfHealMissingThumbnail(category, contextId, fileName, path, activeEncryptionKey, out var healedThumbPath, out regeneratedThumbBytes))
        {
            targetFileName = FileService.GetThumbnailFileName(fileName);
            if (!string.IsNullOrEmpty(healedThumbPath))
            {
                path = healedThumbPath;
            }
        }

        var contentType = _fileService.GetContentType(targetFileName);

        bool forceDownload = (httpContext?.Request?.Query?.ContainsKey("download") == true) || 
                             ForceDownloadExtensions.Contains(extension) || 
                             !SafeInlineContentTypes.Contains(contentType);

        var safeFileName = new string(targetFileName.Select(c => c > 127 ? '_' : c).ToArray());
        var encodedFileName = Uri.EscapeDataString(targetFileName);

        if (httpContext?.Response != null)
        {
            if (forceDownload)
            {
                httpContext.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{safeFileName}\"; filename*=UTF-8''{encodedFileName}");
            }
            else
            {
                httpContext.Response.Headers.Append("Content-Disposition", $"inline; filename=\"{safeFileName}\"; filename*=UTF-8''{encodedFileName}");
            }

            // Add security headers
            httpContext.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            httpContext.Response.Headers.Append("Referrer-Policy", "no-referrer");

            // Prevent CDN caching to ensure the [Authorize] attribute is not bypassed by Edge caches
            // But allow the client's browser/WebView to cache it privately (for 2 weeks) to prevent 
            // slow re-downloading when scrolling on mobile devices.
            httpContext.Response.Headers.Append("Cache-Control", !string.IsNullOrEmpty(activeEncryptionKey) ? "private, no-store" : "private, max-age=1209600");
        }

        if (regeneratedThumbBytes != null)
        {
            return File(new MemoryStream(regeneratedThumbBytes), contentType, forceDownload ? targetFileName : null);
        }

        // 6. Decrypt on the fly if needed
        if (!string.IsNullOrEmpty(activeEncryptionKey))
        {
            try
            {
                var seekableStream = _fileService.OpenDecryptedStream(path, activeEncryptionKey);
                var lastWrite = System.IO.File.GetLastWriteTimeUtc(path);
                var fileLength = new System.IO.FileInfo(path).Length;
                var etag = new EntityTagHeaderValue($"\"{lastWrite.Ticks:x}_{fileLength:x}\"");
                return File(seekableStream, contentType, forceDownload ? targetFileName : null, lastWrite, etag, enableRangeProcessing: true);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogWarning(ex, "Encrypted file not found on disk: {Path}", path);
                return NotFoundError("File not found.", "NOT_FOUND");
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "I/O error opening decrypted stream for file: {Path}", path);
                return StatusCodeError(500, "Disk or storage I/O error occurred while accessing file.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Permission denied accessing encrypted file: {Path}", path);
                return StatusCodeError(500, "Permission denied accessing file storage.");
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
            {
                _logger.LogWarning(ex, "Failed to decrypt channel key or file stream for: {Path}", path);
                return BadRequestError("Failed to decrypt channel key.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error opening decrypted stream for file: {Path}", path);
                return StatusCodeError(500, "An error occurred while opening the decrypted file.");
            }
        }

        if (forceDownload)
        {
            return PhysicalFile(path, contentType, targetFileName, enableRangeProcessing: true);
        }

        return PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    private bool TrySelfHealMissingThumbnail(
        string category,
        string contextId,
        string fileName,
        string originalPath,
        string? encryptionKey,
        out string? healedThumbPath,
        out byte[]? healedThumbBytes)
    {
        healedThumbPath = null;
        healedThumbBytes = null;

        try
        {
            var lockObj = _thumbRegenLocks.GetOrAdd(originalPath, static _ => new object());
            lock (lockObj)
            {
                var canonicalThumbName = FileService.GetThumbnailFileName(fileName);
                var candidatePath = _fileService.GetPhysicalPath(category, contextId, canonicalThumbName);
                if (!string.IsNullOrEmpty(candidatePath) && System.IO.File.Exists(candidatePath))
                {
                    healedThumbPath = candidatePath;
                    return true;
                }

                using Stream? sourceStream = !string.IsNullOrEmpty(encryptionKey)
                    ? _fileService.OpenDecryptedStream(originalPath, encryptionKey)
                    : new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (sourceStream == null) return false;

                using var memoryBuffer = new MemoryStream();
                sourceStream.CopyTo(memoryBuffer);
                memoryBuffer.Position = 0;

                if (!_imageService.TryGenerateThumbnail(memoryBuffer, out var thumbData) || thumbData == null)
                {
                    return false;
                }

                using (thumbData)
                {
                    byte[] thumbBytes;
                    if (thumbData is MemoryStream ms)
                    {
                        thumbBytes = ms.ToArray();
                    }
                    else
                    {
                        using var copyMs = new MemoryStream();
                        thumbData.CopyTo(copyMs);
                        thumbBytes = copyMs.ToArray();
                    }

                    using var uploadStream = new MemoryStream(thumbBytes);
                    _fileService.UploadThumbnailAsync(category, contextId, uploadStream, fileName, encryptionKey)
                        .GetAwaiter()
                        .GetResult();

                    candidatePath = _fileService.GetPhysicalPath(category, contextId, canonicalThumbName);
                    if (!string.IsNullOrEmpty(candidatePath) && System.IO.File.Exists(candidatePath))
                    {
                        healedThumbPath = candidatePath;
                    }
                    else
                    {
                        healedThumbBytes = thumbBytes;
                    }

                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not self-heal thumbnail for {Category}/{ContextId}/{FileName}; falling back to original.", category, contextId, fileName);
            return false;
        }
    }

    /// <summary>
    /// Validates that a path segment doesn't contain traversal attempts or invalid characters
    /// </summary>
    private static bool IsValidPathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;

        // Block path traversal
        if (segment.Contains("..") || segment.Contains('/') || segment.Contains('\\'))
            return false;

        // Block invalid filename characters
        if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        return true;
    }

    private static bool IsMp4Video(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase);
    }
}

public record FileUploadResult(
    string FilePath,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    int? ImageWidth,
    int? ImageHeight,
    bool HasThumbnail,
    double? DurationSeconds = null);
