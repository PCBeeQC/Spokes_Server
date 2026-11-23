using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Filters;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
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
    private readonly ThumbnailCacheService? _thumbnailCache;

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
        IMp4FastStartService? fastStartService = null,
        ThumbnailCacheService? thumbnailCache = null)
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
            fastStartService,
            thumbnailCache)
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
        IMp4FastStartService? fastStartService = null,
        ThumbnailCacheService? thumbnailCache = null)
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
        _thumbnailCache = thumbnailCache;
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

    private static readonly SemaphoreSlim _globalThumbRegenSemaphore = new(Math.Clamp(Environment.ProcessorCount, 2, 4));
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _thumbRegenLocks = new(StringComparer.OrdinalIgnoreCase);

    private record UploadContext(
        Employee User,
        string? EncryptionKey);

    private IActionResult? ValidateUploadPrelude(
        string category,
        string contextId,
        string? token,
        out UploadContext? context,
        bool isThumbnail = false,
        bool allowDemoImport = false)
    {
        context = null;

        if (!IsValidPathSegment(category) || !IsValidPathSegment(contextId))
        {
            return BadRequestError("Invalid path parameters");
        }

        var config = _config;
        if (!allowDemoImport && config?.GetValue<bool>("Spokes_DemoMode") == true)
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

    public const int MaxFilesPerUploadBatch = 50;

    [HttpPost("{category}/{contextId}")]
    [DisableRequestSizeLimit]
    [TypeFilter(typeof(UploadRequestGuardFilter))]
    public async Task<IActionResult> UploadFile(string category, string contextId, [FromForm] IFormFileCollection files, [FromQuery] string? t = null)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequestError("No files uploaded");
        }

        if (files.Count > MaxFilesPerUploadBatch)
        {
            return BadRequestError($"Too many files in a single upload batch (max {MaxFilesPerUploadBatch}).");
        }

        var errorResult = ValidateUploadPrelude(category, contextId, t, out var uploadCtx);
        if (errorResult != null) return errorResult;

        var user = uploadCtx!.User;
        var encryptionKey = uploadCtx.EncryptionKey;

        var profile = _companyProfiles.Get();
        long maxFileSize = profile?.MaxFileUploadSizeBytes ?? WebUiExtensions.DefaultMaxFileUploadSizeBytes;

        bool isMobileUpload = Request.Headers.TryGetValue("X-Upload-Platform", out var platformVal)
            && string.Equals(platformVal, "mobile", StringComparison.OrdinalIgnoreCase);

        var (serverMaxDim, serverQuality) = ResolveUploadImageSettings(category, bypassDownscaling: isMobileUpload, profile);

        long totalBatchBytes = 0;
        foreach (var file in files)
        {
            var sanitizedName = FileHelper.SanitizeDisplayFileName(file.FileName);
            if (file.Length <= 0)
            {
                return BadRequestError($"File {sanitizedName} is empty.");
            }
            if (file.Length > maxFileSize)
            {
                return BadRequestError($"File {sanitizedName} exceeds the max size of {maxFileSize} bytes.");
            }
            totalBatchBytes = totalBatchBytes <= long.MaxValue - file.Length
                ? totalBatchBytes + file.Length
                : long.MaxValue;
        }

        if (_storageHealth is { IsUploadAllowed: true, TotalBytes: > 0, AvailableFreeBytes: > 0 })
        {
            long availableBudget = _storageHealth.AvailableFreeBytes - StorageHealthService.CriticalThresholdBytes;
            if (totalBatchBytes > availableBudget)
            {
                return StatusCodeError(StatusCodes.Status507InsufficientStorage, "Insufficient server disk space to process this upload batch.", "INSUFFICIENT_STORAGE");
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
                    using var sourceStream = file.OpenReadStream();
                    results[index] = await ProcessAndUploadMediaFileAsync(
                        category,
                        contextId,
                        sourceStream,
                        FileHelper.SanitizeDisplayFileName(file.FileName),
                        file.ContentType,
                        file.Length,
                        serverMaxDim,
                        serverQuality,
                        encryptionKey,
                        user.Id);
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

        if (HttpContext?.Response != null)
        {
            HttpContext.Response.Headers["X-Spokes-Media-Scope"] = !string.IsNullOrEmpty(encryptionKey) ? "encrypted" : "standard";
        }

        return Ok(results.ToList());
    }

    [HttpPost("thumbnail/{category}/{contextId}/{fileName}")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024, ValueCountLimit = 8)]
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
    [HttpHead("thumb/{category}/{contextId}/{fileName}")]
    public Task<IActionResult> GetFileThumb(string category, string contextId, string fileName, [FromQuery] string? t = null, [FromQuery] string? s = null)
        => GetFileInternalAsync(category, contextId, fileName, t, thumb: true, smallThumb: string.Equals(s, "sm", StringComparison.OrdinalIgnoreCase), cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

    [HttpGet("{category}/{contextId}/{fileName}")]
    [HttpHead("{category}/{contextId}/{fileName}")]
    public Task<IActionResult> GetFile(string category, string contextId, string fileName, [FromQuery] string? t = null, [FromQuery] string? fn = null)
        => GetFileInternalAsync(category, contextId, fileName, t, thumb: false, displayFileName: fn, cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

    [HttpGet("/internal/attachments/{channelId}/{fileName}")]
    [HttpHead("/internal/attachments/{channelId}/{fileName}")]
    [HttpGet("/spokesapi/attachments/{channelId}/{fileName}")]
    [HttpHead("/spokesapi/attachments/{channelId}/{fileName}")]
    public Task<IActionResult> GetLegacyAttachment(string channelId, string fileName, [FromQuery] string? t = null, [FromQuery] string? fn = null)
        => GetFileInternalAsync("chat", channelId, fileName, t, thumb: false, displayFileName: fn, cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

    [HttpGet("/internal/attachments/thumb/{channelId}/{fileName}")]
    [HttpHead("/internal/attachments/thumb/{channelId}/{fileName}")]
    [HttpGet("/spokesapi/attachments/thumb/{channelId}/{fileName}")]
    [HttpHead("/spokesapi/attachments/thumb/{channelId}/{fileName}")]
    public Task<IActionResult> GetLegacyAttachmentThumb(string channelId, string fileName, [FromQuery] string? t = null, [FromQuery] string? s = null)
        => GetFileInternalAsync("chat", channelId, fileName, t, thumb: true, smallThumb: string.Equals(s, "sm", StringComparison.OrdinalIgnoreCase), cancellationToken: HttpContext?.RequestAborted ?? CancellationToken.None);

    [HttpGet("demo-media/{fileName}")]
    [HttpHead("demo-media/{fileName}")]
    public IActionResult GetDemoMedia(string fileName)
    {
        var config = _config;
        if (config == null || (!config.GetValue<bool>("Spokes_DemoMode") && !config.GetValue<bool>("Spokes_DemoSetup")))
        {
            return NotFoundError("Demo media not available.", "DEMO_MEDIA_NOT_FOUND");
        }

        if (!TryResolveSafeDemoMediaFile(config, fileName, out var fullPath, out var contentType))
        {
            return NotFoundError("Demo media file not found.", "NOT_FOUND");
        }

        if (HttpContext?.Response?.Headers != null)
        {
            HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            HttpContext.Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; sandbox";
            HttpContext.Response.Headers["Priority"] = "u=3, i=?0";
        }

        return PhysicalFile(fullPath, contentType);
    }

    [HttpGet("thumb/demo-media/{fileName}")]
    [HttpHead("thumb/demo-media/{fileName}")]
    public IActionResult GetDemoMediaThumb(string fileName)
        => GetDemoMedia(fileName);

    [HttpPost("demo-media/import/{category}/{contextId}")]
    public async Task<IActionResult> ImportDemoMedia(
        string category,
        string contextId,
        [FromQuery] string fileName,
        [FromQuery] string? t = null)
    {
        var config = _config;
        if (config == null || (!config.GetValue<bool>("Spokes_DemoMode") && !config.GetValue<bool>("Spokes_DemoSetup")))
        {
            return NotFoundError("Demo media not available.", "DEMO_MEDIA_NOT_FOUND");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequestError("FileName is required.");
        }

        if (!TryResolveSafeDemoMediaFile(config, fileName, out var demoMediaPath, out _))
        {
            return NotFoundError("Sample media file not found.", "SAMPLE_FILE_NOT_FOUND");
        }

        fileName = Path.GetFileName(demoMediaPath);

        var errorResult = ValidateUploadPrelude(category, contextId, t, out var uploadCtx, allowDemoImport: true);
        if (errorResult != null) return errorResult;

        var encryptionKey = uploadCtx!.EncryptionKey;
        var profile = _companyProfiles.Get();
        var (serverMaxDim, serverQuality) = ResolveUploadImageSettings(category, bypassDownscaling: false, profile);

        string contentType = _fileService.GetContentType(fileName);
        using var fileStream = new FileStream(demoMediaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await ProcessAndUploadMediaFileAsync(
            category,
            contextId,
            fileStream,
            fileName,
            contentType,
            fileStream.Length,
            serverMaxDim,
            serverQuality,
            encryptionKey,
            uploadCtx.User.Id);

        return Ok(new List<FileUploadResult> { result });
    }

    private (int MaxDim, int Quality) ResolveUploadImageSettings(string category, bool bypassDownscaling, CompanyProfile? profile = null)
    {
        if (bypassDownscaling)
        {
            // Mobile device already applied configured mobile compression settings client-side.
            // Bypass server-side downscaling (preserve uploaded bytes, generate thumbnail).
            return (0, 90);
        }

        profile ??= _companyProfiles.Get();
        if (category.Equals("albums", StringComparison.OrdinalIgnoreCase))
        {
            return (profile?.Albums?.DesktopPhotoMaxDimension ?? 3840, profile?.Albums?.DesktopPhotoQuality ?? 90);
        }

        if (category.Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            return (profile?.Chat?.DesktopPhotoMaxDimension ?? 3840, profile?.Chat?.DesktopPhotoQuality ?? 85);
        }

        return (3840, 90);
    }

    private async Task<FileUploadResult> ProcessAndUploadMediaFileAsync(
        string category,
        string contextId,
        Stream sourceStream,
        string displayFileName,
        string contentType,
        long sourceLength,
        int serverMaxDim,
        int serverQuality,
        string? encryptionKey,
        string userId)
    {
        int? imageWidth = null;
        int? imageHeight = null;
        double? durationSeconds = null;
        bool hasThumbnail = false;
        string safeName = _fileService.GenerateSafeName(displayFileName);
        Stream uploadStream = sourceStream;

        try
        {
            // Skip image/video post-processing for temp files to avoid unnecessary work and orphaned thumbnails.
            if (!category.Equals("temp", StringComparison.OrdinalIgnoreCase) &&
                _imageService.TryProcessUploadImage(uploadStream, contentType, sourceLength, out var processedImage, serverMaxDim, serverQuality))
            {
                if (processedImage.ProcessedStream != sourceStream && processedImage.ProcessedStream != uploadStream)
                {
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

                if (processedImage.SmallThumbnailBytes != null)
                {
                    using var msSmallThumb = new MemoryStream(processedImage.SmallThumbnailBytes);
                    await _fileService.UploadThumbnailAsync(category, contextId, msSmallThumb, safeName, encryptionKey, small: true);
                }
            }
            else if (!category.Equals("temp", StringComparison.OrdinalIgnoreCase) &&
                     _fastStartService != null &&
                     (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || IsMp4Video(displayFileName)))
            {
                if (_fastStartService.TryProcessMp4(uploadStream, out var videoMeta) && videoMeta.Success)
                {
                    if (videoMeta.WasModified && videoMeta.ProcessedStream != null)
                    {
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

            var url = await _fileService.UploadStreamAsync(category, contextId, uploadStream, displayFileName, encryptionKey, safeName);
            var receiptFileName = !string.IsNullOrWhiteSpace(safeName)
                ? safeName
                : Path.GetFileName((url ?? string.Empty).Split('?', '#')[0]);
            var uploadToken = !string.IsNullOrWhiteSpace(receiptFileName)
                ? _fileTokenService?.GenerateUploadReceiptToken(category, contextId, receiptFileName, userId)
                : null;

            return new FileUploadResult(
                url ?? string.Empty,
                displayFileName,
                contentType,
                uploadStream.CanSeek ? uploadStream.Length : sourceLength,
                imageWidth,
                imageHeight,
                hasThumbnail,
                durationSeconds,
                uploadToken,
                IsEncrypted: !string.IsNullOrEmpty(encryptionKey));
        }
        finally
        {
            if (uploadStream != sourceStream)
            {
                uploadStream.Dispose();
            }
        }
    }

    [HttpGet("email/{messageId}/{attachmentId}")]
    [HttpHead("email/{messageId}/{attachmentId}")]
    [HttpGet("/internal/attachments/email/{messageId}/{attachmentId}")]
    [HttpHead("/internal/attachments/email/{messageId}/{attachmentId}")]
    [HttpGet("/spokesapi/attachments/email/{messageId}/{attachmentId}")]
    [HttpHead("/spokesapi/attachments/email/{messageId}/{attachmentId}")]
    public IActionResult GetEmailAttachment(
        string messageId,
        string attachmentId,
        [FromQuery] string? employeeId = null, [FromQuery] string? accountId = null,
        [FromQuery] string? ct = null,
        [FromQuery] string? fn = null)
    {
        // Validate authenticated user (suspended users retain read-only download access)
        var user = _userService.GetEmployee(User);
        if (user == null || !user.IsActive || user.IsBanned)
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        employeeId = !string.IsNullOrEmpty(accountId) ? accountId : employeeId;
        if (string.IsNullOrEmpty(employeeId))
        {
            return BadRequestError("employeeId is required");
        }

        // Prevent path traversal and delimiter injection before scoping access check
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Contains("..") || messageId.Contains('/') || messageId.Contains('\\') ||
            string.IsNullOrWhiteSpace(attachmentId) || attachmentId.Contains("..") || attachmentId.Contains('/') || attachmentId.Contains('\\') ||
            employeeId.Contains("..") || employeeId.Contains('/') || employeeId.Contains('\\') || employeeId.Contains(':'))
        {
            return BadRequestError("Invalid path parameters");
        }

        // Enforce message-scoped ownership via EmailFileAccessProvider ("accountId:messageId")
        if (!_fileAuthService.CanAccess("email", $"{employeeId}:{messageId}", user))
        {
            return ForbiddenError("Access to email attachment forbidden.", "FORBIDDEN");
        }

        var emailInboxService = HttpContext?.RequestServices?.GetService<Spokes_Server.Core.Services.Communication.Email.IEmailInboxService>();
        if (emailInboxService != null && !employeeId.Equals(Spokes_Server.Core.Constants.CompanyBillInboxConstants.SystemEmployeeId, StringComparison.OrdinalIgnoreCase))
        {
            if (!emailInboxService.CanUserAccessEmailMessage(user, employeeId, messageId))
            {
                return ForbiddenError("Access to email attachment forbidden.", "FORBIDDEN");
            }
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
                var attachmentsRootDir = Directory.GetParent(attachmentsDir ?? "")?.FullName;
                var emailAccountDir = Directory.GetParent(attachmentsRootDir ?? "")?.FullName;
                if (!string.IsNullOrEmpty(emailAccountDir))
                {
                    string? mimePath = Path.Combine(emailAccountDir, "Mime", $"{messageId}.eml");
                    if (!System.IO.File.Exists(mimePath))
                    {
                        if (!string.IsNullOrEmpty(attachmentsRootDir))
                        {
                            var legacyMimePath = Path.Combine(attachmentsRootDir, "Mime", $"{messageId}.eml");
                            mimePath = System.IO.File.Exists(legacyMimePath) ? legacyMimePath : null;
                        }
                        else
                        {
                            mimePath = null;
                        }
                    }

                    if (!string.IsNullOrEmpty(mimePath))
                    {
                        using var stream = System.IO.File.OpenRead(mimePath);
                        var mime = MimeKit.MimeMessage.Load(stream);
                        var part = mime.Attachments.OfType<MimeKit.MimePart>()
                            .FirstOrDefault(p => !string.IsNullOrEmpty(fn) && string.Equals(p.FileName, fn, StringComparison.Ordinal));
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

        var originalFileName = !string.IsNullOrEmpty(fn) ? fn : attachmentId;
        var extension = Path.GetExtension(originalFileName);
        var inferredContentType = !string.IsNullOrEmpty(extension)
            ? _fileService.GetContentType(originalFileName)
            : _fileService.GetContentType(filePath);

        string contentType;
        if (!string.IsNullOrWhiteSpace(ct) && !ct.Contains('\r') && !ct.Contains('\n'))
        {
            contentType = ct.Trim();
            if (SafeInlineContentTypes.Contains(contentType) &&
                !string.IsNullOrEmpty(extension) &&
                !string.Equals(inferredContentType, contentType, StringComparison.OrdinalIgnoreCase))
            {
                contentType = inferredContentType;
            }
        }
        else
        {
            contentType = inferredContentType;
        }

        Response.Headers.Append("X-Content-Type-Options", "nosniff");
        Response.Headers.Append("Referrer-Policy", "no-referrer");
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["Vary"] = "Cookie, Authorization";
        Response.Headers["Priority"] = "u=3, i=?0";

        bool forceDownload = Request.Query.ContainsKey("download") || 
                             ForceDownloadExtensions.Contains(extension) || 
                             !SafeInlineContentTypes.Contains(contentType);
        bool enableRange = SupportsByteRangeProcessing(contentType, forceDownload);

        // Support download mode or force download for dangerous types
        if (forceDownload)
        {
            return PhysicalFile(filePath, contentType, originalFileName, enableRangeProcessing: enableRange);
        }

        // Serve inline — set Content-Disposition: inline to prevent browser auto-download
        // Use ASCII-safe filename + RFC 5987 UTF-8 encoding for non-ASCII characters
        var safeFileName = new string(originalFileName.Select(c => c > 127 ? '_' : c).ToArray());
        var encodedFileName = Uri.EscapeDataString(originalFileName);
        Response.Headers.ContentDisposition = $"inline; filename=\"{safeFileName}\"; filename*=UTF-8''{encodedFileName}";
        return PhysicalFile(filePath, contentType, enableRangeProcessing: enableRange);
    }

    private Task<IActionResult> GetFileInternalAsync(
        string category,
        string contextId,
        string fileName,
        string? t,
        bool thumb,
        string? displayFileName = null,
        bool smallThumb = false,
        CancellationToken cancellationToken = default)
    {
        // 0. Validate route parameters - prevent path traversal
        if (!IsValidPathSegment(category) || !IsValidPathSegment(contextId) || !IsValidPathSegment(fileName))
        {
            return Task.FromResult<IActionResult>(BadRequestError("Invalid path parameters"));
        }

        fileName = Path.GetFileName(fileName);

        // 1. Identify User (always available from [Authorize] middleware; suspended users retain read-only access)
        var user = _userService.GetEmployee(User);
        if (user == null || !user.IsActive || user.IsBanned)
        {
            return Task.FromResult<IActionResult>(UnauthorizedError("Authentication required."));
        }

        var sessionId = User.FindFirst("SessionId")?.Value;

        // Require BOTH [Authorize] (authenticated active user) AND a valid user+session+file-scoped access token.
        if (_fileTokenService == null)
        {
            return Task.FromResult<IActionResult>(StatusCodeError(500, "File token service unavailable."));
        }

        var tokenValidation = _fileTokenService.ValidateAccessToken(
            category,
            contextId,
            fileName,
            t,
            user.Id,
            sessionId,
            isThumbnail: thumb,
            requireToken: true);

        if (tokenValidation.Status == FileTokenStatus.Missing)
        {
            return Task.FromResult<IActionResult>(ForbiddenError("A valid file access token is required.", "TOKEN_REQUIRED"));
        }

        if (!tokenValidation.IsSuccess)
        {
            return Task.FromResult<IActionResult>(StatusCodeError(410, "File Token Expired or Invalid"));
        }
        string? tokenEncryptionKey = tokenValidation.EncryptionKey;

        return ServeFileForUserAsync(category, contextId, fileName, user, thumb, tokenEncryptionKey, displayFileName, smallThumb, cancellationToken);
    }

    /// <summary>
    /// Serves an authorized file for an authenticated user with permission checks and on-the-fly decryption.
    /// Can be called directly by user-scoped session bridges (e.g. slideshow presentations) to ensure
    /// the full FileController authorization and decryption pipeline is applied.
    /// </summary>
    [NonAction]
    internal async Task<IActionResult> ServeFileForUserAsync(
        string category,
        string contextId,
        string fileName,
        Employee user,
        bool thumb,
        string? tokenEncryptionKey = null,
        string? displayFileName = null,
        bool smallThumb = false,
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();

        if (!IsValidPathSegment(category) || !IsValidPathSegment(contextId) || !IsValidPathSegment(fileName))
        {
            return BadRequestError("Invalid path parameters");
        }

        fileName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(fileName);

        if (user == null || !user.IsActive || user.IsBanned)
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
        if (category.Equals("chat", StringComparison.OrdinalIgnoreCase) && _channels.GetById(contextId) == null)
        {
            return NotFoundError("Channel not found");
        }

        // If no provider is defined for this category, deny access by default for security
        if (!_fileAuthService.IsKnownCategory(category))
        {
            return BadRequestError($"Unknown file category: {category}");
        }

        if (!_fileAuthService.CanAccess(category, contextId, user))
        {
            return ForbiddenError("Access forbidden.", "FORBIDDEN");
        }

        if (category.Equals("albums", StringComparison.OrdinalIgnoreCase) &&
            !thumb &&
            !FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Albums.View))
        {
            return ForbiddenError("Access forbidden.", "FORBIDDEN");
        }

        double authMs = totalSw.Elapsed.TotalMilliseconds;

        // 4. Serve File Securely
        bool effectiveSmallThumb = thumb && smallThumb;
        var targetFileName = thumb ? FileService.GetThumbnailFileName(fileName, effectiveSmallThumb) : fileName;
        var path = _fileService.GetPhysicalPath(category, contextId, targetFileName);
        bool fellBackToOriginalForThumb = false;
        bool needsSelfHealThumb = false;
        CachedThumbnailEntry? cachedTargetThumb = null;
        CachedThumbnailEntry? cachedSourceThumb = null;
        System.IO.FileInfo? resolvedFileInfo = null;

        if (thumb && _thumbnailCache != null && _thumbnailCache.TryGet(path, out cachedTargetThumb) && cachedTargetThumb != null)
        {
            // Hot RAM cache hit for exact requested thumbnail variant (0 disk syscalls).
        }
        else
        {
            resolvedFileInfo = TryGetExistingFileInfo(path);

            if (thumb && resolvedFileInfo == null)
            {
                // When requesting a small grid thumbnail (?s=sm) that doesn't exist yet, prefer deriving it
                // from the existing standard _thumb.jpg (fast 1280px -> 400px resize) instead of decoding the 4K original.
                if (effectiveSmallThumb)
                {
                    var standardThumbName = FileService.GetThumbnailFileName(fileName, small: false);
                    var standardThumbPath = _fileService.GetPhysicalPath(category, contextId, standardThumbName);
                    if (_thumbnailCache != null && _thumbnailCache.TryGet(standardThumbPath, out var cachedStd) && cachedStd != null)
                    {
                        path = standardThumbPath;
                        targetFileName = standardThumbName;
                        cachedSourceThumb = cachedStd;
                        needsSelfHealThumb = true;
                    }
                    else
                    {
                        var stdInfo = TryGetExistingFileInfo(standardThumbPath);
                        if (stdInfo != null)
                        {
                            path = standardThumbPath;
                            targetFileName = standardThumbName;
                            resolvedFileInfo = stdInfo;
                            needsSelfHealThumb = true;
                        }
                    }
                }

                if (cachedSourceThumb == null && resolvedFileInfo == null && category.Equals("chat", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var legacyPath = _fileService.GetAttachmentPhysicalPath(contextId, fileName, thumb: true);
                        var legacyInfo = TryGetExistingFileInfo(legacyPath);
                        if (legacyInfo != null)
                        {
                            path = legacyPath;
                            targetFileName = Path.GetFileName(legacyPath);
                            resolvedFileInfo = legacyInfo;
                            if (!targetFileName.EndsWith("_thumb.jpg", StringComparison.OrdinalIgnoreCase))
                            {
                                fellBackToOriginalForThumb = true;
                                needsSelfHealThumb = true;
                            }
                            else if (effectiveSmallThumb)
                            {
                                needsSelfHealThumb = true;
                            }
                        }
                    }
                    catch (ArgumentException) { }
                }

                if (cachedSourceThumb == null && resolvedFileInfo == null)
                {
                    // Only allow falling back to original file if the original file itself is a safe image format.
                    // Videos, archives, audio, and documents must never stream as thumbnail responses.
                    if (SafeThumbnailFallbackExtensions.Contains(extension))
                    {
                        fellBackToOriginalForThumb = true;
                        needsSelfHealThumb = true;
                        targetFileName = fileName;
                        path = _fileService.GetPhysicalPath(category, contextId, targetFileName);
                        resolvedFileInfo = TryGetExistingFileInfo(path);

                        if (resolvedFileInfo == null && category.Equals("chat", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var legacyPath = _fileService.GetAttachmentPhysicalPath(contextId, fileName, thumb: false);
                                var legacyInfo = TryGetExistingFileInfo(legacyPath);
                                if (legacyInfo != null)
                                {
                                    path = legacyPath;
                                    targetFileName = Path.GetFileName(legacyPath);
                                    resolvedFileInfo = legacyInfo;
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
            else if (!thumb && resolvedFileInfo == null && category.Equals("chat", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var legacyPath = _fileService.GetAttachmentPhysicalPath(contextId, fileName, thumb: false);
                    var legacyInfo = TryGetExistingFileInfo(legacyPath);
                    if (legacyInfo != null)
                    {
                        path = legacyPath;
                        targetFileName = Path.GetFileName(legacyPath);
                        resolvedFileInfo = legacyInfo;
                    }
                }
                catch (ArgumentException) { }
            }

            if (cachedSourceThumb == null && resolvedFileInfo == null)
            {
                return NotFoundError("File not found.", "NOT_FOUND");
            }
        }

        var httpContext = ControllerContext?.HttpContext;
        string? activeEncryptionKey = null;

        // 5. Resolve encryption key if needed
        double keyStartMs = totalSw.Elapsed.TotalMilliseconds;
        if (category.Equals("chat", StringComparison.OrdinalIgnoreCase) || category.Equals("albums", StringComparison.OrdinalIgnoreCase))
        {
            var keyResult = category.Equals("chat", StringComparison.OrdinalIgnoreCase)
                ? _fileAuthService.ResolveDownloadKey(category, contextId, user, tokenEncryptionKey, httpContext, targetFileName)
                : _fileAuthService.ResolveDownloadKey(category, contextId, user, tokenEncryptionKey, httpContext);
            if (!keyResult.IsSuccess)
            {
                return BadRequestError(keyResult.ErrorMessage ?? "Access denied.");
            }

            if (keyResult.IsEncrypted && !string.IsNullOrEmpty(keyResult.EncryptionKey))
            {
                activeEncryptionKey = keyResult.EncryptionKey;
            }
        }
        double keyMs = Math.Max(0, totalSw.Elapsed.TotalMilliseconds - keyStartMs);

        // 5.5 Self-heal missing thumbnail asynchronously under bounded concurrency so subsequent requests serve it directly
        byte[]? regeneratedThumbBytes = null;
        bool hasSelfHealedThumbnail = false;
        double selfHealMs = 0;
        if (needsSelfHealThumb && !user.IsSuspended)
        {
            double healStartMs = totalSw.Elapsed.TotalMilliseconds;
            var (healed, healedThumbPath, healedBytes, healedCachedEntry) = await TrySelfHealMissingThumbnailAsync(
                category,
                contextId,
                fileName,
                path,
                cachedSourceThumb,
                activeEncryptionKey,
                effectiveSmallThumb,
                cancellationToken);
            selfHealMs = Math.Max(0, totalSw.Elapsed.TotalMilliseconds - healStartMs);
            if (healed)
            {
                hasSelfHealedThumbnail = true;
                targetFileName = FileService.GetThumbnailFileName(fileName, effectiveSmallThumb);
                if (!string.IsNullOrEmpty(healedThumbPath))
                {
                    path = healedThumbPath;
                    resolvedFileInfo = null;
                }
                regeneratedThumbBytes = healedBytes;
                if (healedCachedEntry != null)
                {
                    cachedTargetThumb = healedCachedEntry;
                }
            }
        }

        if (!hasSelfHealedThumbnail && cachedTargetThumb == null && cachedSourceThumb != null)
        {
            cachedTargetThumb = cachedSourceThumb;
        }

        if (fellBackToOriginalForThumb &&
            !hasSelfHealedThumbnail &&
            category.Equals("albums", StringComparison.OrdinalIgnoreCase) &&
            !FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Albums.View))
        {
            return NotFoundError("Thumbnail not found.", "NOT_FOUND");
        }

        var contentType = _fileService.GetContentType(targetFileName);

        bool forceDownload = (httpContext?.Request?.Query?.ContainsKey("download") == true) || 
                             ForceDownloadExtensions.Contains(extension) || 
                             !SafeInlineContentTypes.Contains(contentType);

        var responseDisplayFileName = ResolveSafeDisplayFileName(displayFileName, targetFileName);
        var safeFileName = new string(responseDisplayFileName.Select(c => c > 127 ? '_' : c).ToArray());
        var encodedFileName = Uri.EscapeDataString(responseDisplayFileName);

        bool isEncryptedFile = !string.IsNullOrEmpty(activeEncryptionKey);

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

            // Prevent CDN caching so [Authorize] + token checks are never bypassed by edge caches.
            // Unencrypted files may be cached privately in HTTP caches for 2 weeks; encrypted files use
            // "private, no-store" so browser HTTP caches never keep unpurgable copies—encrypted media caching
            // is owned exclusively by Spokes' client cache (mediaLoader.js) and purged when the vault password
            // is removed from the device.
            httpContext.Response.Headers["Cache-Control"] = isEncryptedFile ? "private, no-store" : "private, max-age=1209600";
            httpContext.Response.Headers["X-Spokes-Media-Scope"] = isEncryptedFile ? "encrypted" : "standard";

            // RFC 9218 Non-Incremental HTTP/2 & HTTP/3 stream priority ("u=3, i=?0").
            // Explicitly setting i=?0 (false) is required per RFC 9218 §8 so reverse proxies/CDNs (e.g. Cloudflare cfExtPri)
            // override the browser's default "Priority: u=1, i" request header and stream each image sequentially
            // to completion instead of round-robin interleaving small DATA frames across concurrent streams.
            httpContext.Response.Headers["Priority"] = "u=3, i=?0";
        }

        bool enableRange = SupportsByteRangeProcessing(contentType, forceDownload);

        void EmitServerTiming(string modeDesc)
        {
            double totalMs = totalSw.Elapsed.TotalMilliseconds;
            if (httpContext?.Response != null)
            {
                var inv = CultureInfo.InvariantCulture;
                var parts = new List<string>(4)
                {
                    string.Create(inv, $"auth;dur={authMs:F1}"),
                    string.Create(inv, $"key;dur={keyMs:F1}")
                };
                if (hasSelfHealedThumbnail || selfHealMs > 0)
                {
                    parts.Add(string.Create(inv, $"thumb;dur={selfHealMs:F1};desc=\"{(hasSelfHealedThumbnail ? "self-heal" : "heal-miss")}\""));
                }
                parts.Add(string.Create(inv, $"total;dur={totalMs:F1};desc=\"{modeDesc}\""));
                httpContext.Response.Headers["Server-Timing"] = string.Join(", ", parts);
            }

            if (totalMs >= 250)
            {
                _logger.LogWarning(
                    "[Perf][FileController] Slow media serve ({Mode}): category={Category} thumb={Thumb} small={Small} authMs={AuthMs:F1} keyMs={KeyMs:F1} selfHealMs={SelfHealMs:F1} totalMs={TotalMs:F1}",
                    modeDesc, category, thumb, effectiveSmallThumb, authMs, keyMs, selfHealMs, totalMs);
            }
        }

        // Fast path A: Serve freshly self-healed thumbnail bytes straight from RAM without re-reading disk.
        if (regeneratedThumbBytes != null)
        {
            var regenLastWrite = cachedTargetThumb?.LastWriteUtc ?? DateTimeOffset.UtcNow;
            var regenEtagValue = cachedTargetThumb?.ETag ?? ThumbnailCacheService.BuildETag(regenLastWrite, regeneratedThumbBytes.Length);
            if (cachedTargetThumb == null && _thumbnailCache != null && !isEncryptedFile)
            {
                _thumbnailCache.Set(path, regeneratedThumbBytes, regenLastWrite, contentType);
            }
            EmitServerTiming("self-heal");
            var regenStream = new MemoryStream(regeneratedThumbBytes, writable: false);
            if (isEncryptedFile)
            {
                return File(regenStream, contentType, forceDownload ? responseDisplayFileName : null, enableRangeProcessing: enableRange);
            }
            return File(
                regenStream,
                contentType,
                forceDownload ? responseDisplayFileName : null,
                regenLastWrite,
                new EntityTagHeaderValue(regenEtagValue),
                enableRangeProcessing: enableRange);
        }

        // Fast path B: Serve cached thumbnail directly from bounded LRU RAM cache (0 disk syscalls).
        if (cachedTargetThumb != null)
        {
            var cachedLastWrite = cachedTargetThumb.LastWriteUtc;
            var cachedEtagValue = cachedTargetThumb.ETag;
            var cachedEtag = new EntityTagHeaderValue(cachedEtagValue);

            if (httpContext != null &&
                !httpContext.Request.Headers.ContainsKey(HeaderNames.IfRange) &&
                !httpContext.Request.Headers.ContainsKey(HeaderNames.Range))
            {
                var reqMethod = httpContext.Request.Method;
                bool isGetOrHead = HttpMethods.IsGet(reqMethod) || HttpMethods.IsHead(reqMethod);
                if (!isEncryptedFile &&
                    isGetOrHead &&
                    httpContext.Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatchValues) &&
                    MatchesEntityTag(ifNoneMatchValues, cachedEtagValue))
                {
                    httpContext.Response.Headers[HeaderNames.ETag] = cachedEtagValue;
                    httpContext.Response.Headers[HeaderNames.LastModified] = cachedLastWrite.ToString("R");
                    EmitServerTiming("mem-304");
                    return StatusCode(StatusCodes.Status304NotModified);
                }

                if (isEncryptedFile &&
                    HttpMethods.IsHead(reqMethod))
                {
                    httpContext.Response.ContentType = contentType;
                    if (enableRange)
                    {
                        httpContext.Response.Headers[HeaderNames.AcceptRanges] = "bytes";
                    }
                    EmitServerTiming("mem-head");
                    return Ok();
                }
            }

            if (isEncryptedFile)
            {
                try
                {
                    var seekableStream = _fileService.OpenDecryptedStream(cachedTargetThumb.RawBytes, activeEncryptionKey!);
                    EmitServerTiming("mem-enc");
                    return File(seekableStream, contentType, forceDownload ? responseDisplayFileName : null, enableRangeProcessing: enableRange);
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
                {
                    _logger.LogWarning(ex, "Failed to decrypt cached thumbnail stream for: {Path}", path);
                    return BadRequestError("Failed to decrypt channel key.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error decrypting cached thumbnail stream for: {Path}", path);
                    return StatusCodeError(500, "An error occurred while opening the decrypted file.");
                }
            }

            EmitServerTiming("mem");
            return File(
                new MemoryStream(cachedTargetThumb.RawBytes, writable: false),
                contentType,
                forceDownload ? responseDisplayFileName : null,
                cachedLastWrite,
                cachedEtag,
                enableRangeProcessing: enableRange);
        }

        // Disk path: reuse single stat() FileInfo collected during resolution.
        var fileInfo = resolvedFileInfo ?? new System.IO.FileInfo(path);
        var lastWrite = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);
        var fileLength = fileInfo.Length;
        var etagValue = ThumbnailCacheService.BuildETag(lastWrite, fileLength);
        var etag = new EntityTagHeaderValue(etagValue);

        if (httpContext != null &&
            !httpContext.Request.Headers.ContainsKey(HeaderNames.IfRange) &&
            !httpContext.Request.Headers.ContainsKey(HeaderNames.Range))
        {
            var reqMethod = httpContext.Request.Method;
            bool isGetOrHead = HttpMethods.IsGet(reqMethod) || HttpMethods.IsHead(reqMethod);
            if (!isEncryptedFile &&
                isGetOrHead &&
                httpContext.Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatchValues) &&
                MatchesEntityTag(ifNoneMatchValues, etagValue))
            {
                if (thumb && !fellBackToOriginalForThumb && _thumbnailCache != null && fileLength > 0 && fileLength <= _thumbnailCache.MaxEntryBytes)
                {
                    _thumbnailCache.WarmFromDiskInBackground(path, lastWrite, fileLength, contentType);
                }
                httpContext.Response.Headers[HeaderNames.ETag] = etagValue;
                httpContext.Response.Headers[HeaderNames.LastModified] = lastWrite.ToString("R");
                EmitServerTiming("304");
                return StatusCode(StatusCodes.Status304NotModified);
            }

            if (isEncryptedFile &&
                HttpMethods.IsHead(reqMethod))
            {
                httpContext.Response.ContentType = contentType;
                if (enableRange)
                {
                    httpContext.Response.Headers[HeaderNames.AcceptRanges] = "bytes";
                }
                EmitServerTiming("head");
                return Ok();
            }
        }

        // Cold thumbnail cache fill: read thumbnail bytes once into RAM cache so subsequent requests & 304s hit RAM.
        if (thumb && !fellBackToOriginalForThumb && _thumbnailCache != null && fileLength > 0 && fileLength <= _thumbnailCache.MaxEntryBytes)
        {
            try
            {
                long cacheEpoch = _thumbnailCache.CaptureEpoch();
                var rawBytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                _thumbnailCache.TrySetIfUnchanged(path, rawBytes, lastWrite, contentType, cacheEpoch);

                if (isEncryptedFile)
                {
                    var seekableStream = _fileService.OpenDecryptedStream(rawBytes, activeEncryptionKey!);
                    EmitServerTiming("disk-enc-cached");
                    return File(seekableStream, contentType, forceDownload ? responseDisplayFileName : null, enableRangeProcessing: enableRange);
                }

                EmitServerTiming("disk-cached");
                return File(
                    new MemoryStream(rawBytes, writable: false),
                    contentType,
                    forceDownload ? responseDisplayFileName : null,
                    lastWrite,
                    etag,
                    enableRangeProcessing: enableRange);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogWarning(ex, "Thumbnail file not found on disk: {Path}", path);
                return NotFoundError("File not found.", "NOT_FOUND");
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "I/O error reading thumbnail file: {Path}", path);
                return StatusCodeError(500, "Disk or storage I/O error occurred while accessing file.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Permission denied accessing thumbnail file: {Path}", path);
                return StatusCodeError(500, "Permission denied accessing file storage.");
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
            {
                _logger.LogWarning(ex, "Failed to decrypt channel key or thumbnail stream for: {Path}", path);
                return BadRequestError("Failed to decrypt channel key.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error serving thumbnail file: {Path}", path);
                return StatusCodeError(500, "An error occurred while opening the file.");
            }
        }

        // 6. Decrypt on the fly if needed
        if (isEncryptedFile)
        {
            try
            {
                var seekableStream = _fileService.OpenDecryptedStream(path, activeEncryptionKey!);
                EmitServerTiming("enc-stream");
                return File(seekableStream, contentType, forceDownload ? responseDisplayFileName : null, enableRangeProcessing: enableRange);
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

        EmitServerTiming("disk");
        if (forceDownload)
        {
            return PhysicalFile(path, contentType, responseDisplayFileName, lastWrite, etag, enableRangeProcessing: enableRange);
        }

        return PhysicalFile(path, contentType, lastWrite, etag, enableRangeProcessing: enableRange);
    }

    private static System.IO.FileInfo? TryGetExistingFileInfo(string? physicalPath)
    {
        if (string.IsNullOrWhiteSpace(physicalPath)) return null;
        var info = new System.IO.FileInfo(physicalPath);
        return info.Exists ? info : null;
    }

    private static bool SupportsByteRangeProcessing(string? contentType, bool forceDownload) =>
        forceDownload ||
        string.IsNullOrWhiteSpace(contentType) ||
        !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesEntityTag(Microsoft.Extensions.Primitives.StringValues headerValues, string etagValue)
    {
        for (int i = 0; i < headerValues.Count; i++)
        {
            var raw = headerValues[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part == "*") return true;
                var candidate = part.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? part[2..].Trim() : part;
                if (string.Equals(candidate, etagValue, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string ResolveSafeDisplayFileName(string? displayFileName, string targetFileName)
    {
        if (string.IsNullOrWhiteSpace(displayFileName))
            return targetFileName;

        var sanitizedDisplay = Spokes_Server.Core.Helpers.FileHelper.SanitizeDisplayFileName(displayFileName, string.Empty);
        if (string.IsNullOrWhiteSpace(sanitizedDisplay))
            return targetFileName;

        var targetExt = Path.GetExtension(targetFileName);
        var displayExt = Path.GetExtension(sanitizedDisplay);
        var extensionsMatch = string.IsNullOrEmpty(targetExt)
            ? string.IsNullOrEmpty(displayExt)
            : string.Equals(targetExt, displayExt, StringComparison.OrdinalIgnoreCase);

        return extensionsMatch ? sanitizedDisplay : targetFileName;
    }

    private async Task<(bool Success, string? HealedThumbPath, byte[]? HealedThumbBytes, CachedThumbnailEntry? CachedEntry)> TrySelfHealMissingThumbnailAsync(
        string category,
        string contextId,
        string fileName,
        string sourcePath,
        CachedThumbnailEntry? cachedSourceThumb,
        string? encryptionKey,
        bool smallThumb,
        CancellationToken cancellationToken)
    {
        var lockKey = $"{sourcePath}|{(smallThumb ? "sm" : "std")}";
        var pathLock = _thumbRegenLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        bool pathLockAcquired = false;
        bool globalLockAcquired = false;

        try
        {
            await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            pathLockAcquired = true;

            var canonicalThumbName = FileService.GetThumbnailFileName(fileName, smallThumb);
            var candidatePath = _fileService.GetPhysicalPath(category, contextId, canonicalThumbName);
            if (!string.IsNullOrEmpty(candidatePath))
            {
                if (_thumbnailCache != null && _thumbnailCache.TryGet(candidatePath, out var existingCached) && existingCached != null)
                {
                    return (true, candidatePath, null, existingCached);
                }
                if (System.IO.File.Exists(candidatePath))
                {
                    return (true, candidatePath, null, null);
                }
            }

            await _globalThumbRegenSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            globalLockAcquired = true;

            // Re-check after acquiring global concurrency slot
            if (!string.IsNullOrEmpty(candidatePath))
            {
                if (_thumbnailCache != null && _thumbnailCache.TryGet(candidatePath, out var existingCached) && existingCached != null)
                {
                    return (true, candidatePath, null, existingCached);
                }
                if (System.IO.File.Exists(candidatePath))
                {
                    return (true, candidatePath, null, null);
                }
            }

            using var memoryBuffer = new MemoryStream();
            if (cachedSourceThumb != null)
            {
                await using Stream sourceStream = !string.IsNullOrEmpty(encryptionKey)
                    ? _fileService.OpenDecryptedStream(cachedSourceThumb.RawBytes, encryptionKey)
                    : new MemoryStream(cachedSourceThumb.RawBytes, writable: false);
                await sourceStream.CopyToAsync(memoryBuffer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using (Stream? sourceStream = !string.IsNullOrEmpty(encryptionKey)
                    ? _fileService.OpenDecryptedStream(sourcePath, encryptionKey)
                    : new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true))
                {
                    if (sourceStream == null) return (false, null, null, null);
                    await sourceStream.CopyToAsync(memoryBuffer, cancellationToken).ConfigureAwait(false);
                }
            }

            memoryBuffer.Position = 0;

            int maxDim = smallThumb ? ImageProcessingService.SmallThumbMaxDim : ImageProcessingService.StandardThumbMaxDim;
            int quality = smallThumb ? ImageProcessingService.SmallThumbQuality : ImageProcessingService.StandardThumbQuality;

            byte[]? thumbBytes = await Task.Run(() =>
            {
                if (!_imageService.TryGenerateThumbnail(memoryBuffer, out var thumbData, maxDim, quality) || thumbData == null)
                {
                    return null;
                }

                using (thumbData)
                {
                    if (thumbData is MemoryStream ms)
                    {
                        return ms.ToArray();
                    }

                    using var copyMs = new MemoryStream();
                    thumbData.CopyTo(copyMs);
                    return copyMs.ToArray();
                }
            }, cancellationToken).ConfigureAwait(false);

            if (thumbBytes == null || thumbBytes.Length == 0)
            {
                return (false, null, null, null);
            }

            using var uploadStream = new MemoryStream(thumbBytes, writable: false);
            await _fileService.UploadThumbnailAsync(category, contextId, uploadStream, fileName, encryptionKey, small: smallThumb)
                .ConfigureAwait(false);

            CachedThumbnailEntry? newlyCached = null;
            if (!string.IsNullOrEmpty(candidatePath) && _thumbnailCache != null)
            {
                _thumbnailCache.TryGet(candidatePath, out newlyCached);
            }

            // Always return the freshly generated bytes directly in RAM so ServeFileForUserAsync
            // serves the response immediately without re-reading slow disk storage.
            return (true, candidatePath, thumbBytes, newlyCached);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, null, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not self-heal thumbnail ({Size}) for {Category}/{ContextId}/{FileName}; falling back.", smallThumb ? "sm" : "std", category, contextId, fileName);
            return (false, null, null, null);
        }
        finally
        {
            if (globalLockAcquired) _globalThumbRegenSemaphore.Release();
            if (pathLockAcquired) pathLock.Release();
        }
    }

    private static bool TryResolveSafeDemoMediaFile(
        IConfiguration config,
        string? fileName,
        out string fullPath,
        out string contentType)
    {
        fullPath = string.Empty;
        contentType = string.Empty;

        if (string.IsNullOrWhiteSpace(fileName) ||
            !IsValidPathSegment(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return false;
        }

        var mappedType = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null
        };

        if (mappedType == null)
        {
            return false;
        }

        var dataPath = config["DataPath"] ?? "Data";
        var demoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dataPath, "DemoMedia"))) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(demoRoot, fileName));
        if (!candidate.StartsWith(demoRoot, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(candidate))
        {
            return false;
        }

        fullPath = candidate;
        contentType = mappedType;
        return true;
    }

    /// <summary>
    /// Validates that a path segment doesn't contain traversal attempts or invalid characters
    /// </summary>
    internal static bool IsValidPathSegment(string segment) => FileHelper.IsValidPathSegment(segment);

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
    double? DurationSeconds = null,
    string? UploadToken = null,
    bool IsEncrypted = false);
