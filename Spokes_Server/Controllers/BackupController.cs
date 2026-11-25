using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Maintenance;

namespace Spokes_Server.Controllers;

[Route("spokesapi/backups")]
[ApiController]
[Authorize(Policy = AppPermissions.Admin.PolicyName)]
public class BackupController : SpokesControllerBase
{
    private readonly BackupService _backupService;
    private readonly IConfiguration _config;
    private readonly UserService? _userService;
    private readonly BackupRestoreUploadService? _uploadService;

    public BackupController(
        BackupService backupService,
        IConfiguration config,
        UserService? userService = null,
        BackupRestoreUploadService? uploadService = null)
    {
        _backupService = backupService;
        _config = config;
        _userService = userService;
        _uploadService = uploadService;
    }

    private IActionResult? ValidateAdminBackupAccess(string action, out Employee? caller)
    {
        caller = _userService?.GetEmployee(User);
        if (caller is not { IsActiveUnsuspended: true } || !caller.IsEffectiveAdmin)
        {
            return ForbiddenError($"Administrator access is required to {action}.", "ADMIN_REQUIRED");
        }

        if (_config.GetValue<bool>("Spokes_DemoMode"))
        {
            return ForbiddenError($"Backups cannot be {action} in demo mode.", "DEMO_MODE_FORBIDDEN");
        }

        return null;
    }

    private IActionResult MapUploadError(string? message, string? code) => code switch
    {
        "SESSION_NOT_FOUND" => NotFoundError(message ?? "Upload session not found.", code),
        "SESSION_FORBIDDEN" => ForbiddenError(message ?? "Upload session forbidden.", code),
        "INSUFFICIENT_STORAGE" => StatusCodeError(StatusCodes.Status507InsufficientStorage, message ?? "Insufficient storage.", code),
        "TOO_MANY_UPLOADS" => StatusCodeError(StatusCodes.Status429TooManyRequests, message ?? "Another upload is already in progress.", code),
        _ => BadRequestError(message ?? "Backup upload failed.", code)
    };

    [HttpGet("{fileName}")]
    [Authorize(Policy = AppPermissions.Admin.PolicyName)]
    public IActionResult DownloadBackup(string fileName)
    {
        var accessError = ValidateAdminBackupAccess("download backups", out _);
        if (accessError != null) return accessError;

        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
        {
            return BadRequestError("Invalid file name");
        }

        var backups = _backupService.GetBackups();
        var backup = backups.FirstOrDefault(b => b.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        if (backup == null || !System.IO.File.Exists(backup.FilePath))
        {
            return NotFoundError("Backup file not found.", "NOT_FOUND");
        }

        var contentType = backup.FileName.EndsWith(".spkbackup", StringComparison.OrdinalIgnoreCase)
            ? "application/octet-stream"
            : "application/zip";

        return PhysicalFile(backup.FilePath, contentType, backup.FileName, enableRangeProcessing: true);
    }

    [HttpPost("restore-upload/init")]
    [Authorize(Policy = AppPermissions.Admin.PolicyName)]
    public IActionResult InitRestoreUpload([FromBody] BackupUploadInitRequest? request)
    {
        var accessError = ValidateAdminBackupAccess("restore backups", out var caller);
        if (accessError != null) return accessError;
        if (_uploadService == null) return StatusCodeError(StatusCodes.Status503ServiceUnavailable, "Backup upload service unavailable.");
        if (request == null) return BadRequestError("Upload initialization payload is required.");

        var result = _uploadService.InitSession(request.FileName, request.TotalBytes, request.ChunkSizeBytes, BackupUploadScope.Admin, caller!.Id);
        if (!result.Success) return MapUploadError(result.ErrorMessage, result.ErrorCode);

        return Ok(new
        {
            uploadId = result.UploadId,
            chunkSizeBytes = result.ChunkSizeBytes,
            totalChunks = result.TotalChunks
        });
    }

    [HttpPut("restore-upload/{uploadId}/chunks/{chunkIndex:int}")]
    [Authorize(Policy = AppPermissions.Admin.PolicyName)]
    [RequestSizeLimit(BackupRestoreUploadService.MaxChunkSizeBytes + 1024 * 1024)]
    public async Task<IActionResult> UploadRestoreChunk(string uploadId, int chunkIndex, CancellationToken ct)
    {
        var accessError = ValidateAdminBackupAccess("restore backups", out var caller);
        if (accessError != null) return accessError;
        if (_uploadService == null) return StatusCodeError(StatusCodes.Status503ServiceUnavailable, "Backup upload service unavailable.");

        var result = await _uploadService.AppendChunkAsync(
            uploadId,
            chunkIndex,
            Request.Body,
            Request.ContentLength,
            BackupUploadScope.Admin,
            caller!.Id,
            ct);

        if (!result.Success) return MapUploadError(result.ErrorMessage, result.ErrorCode);

        return Ok(new
        {
            chunkIndex = result.ChunkIndex,
            bytesReceived = result.BytesReceived,
            expectedTotalBytes = result.ExpectedTotalBytes
        });
    }

    [HttpPost("restore-upload/{uploadId}/complete")]
    [Authorize(Policy = AppPermissions.Admin.PolicyName)]
    public async Task<IActionResult> CompleteRestoreUpload(string uploadId, CancellationToken ct)
    {
        var accessError = ValidateAdminBackupAccess("restore backups", out var caller);
        if (accessError != null) return accessError;
        if (_uploadService == null) return StatusCodeError(StatusCodes.Status503ServiceUnavailable, "Backup upload service unavailable.");

        var result = await _uploadService.CompleteSessionAsync(uploadId, BackupUploadScope.Admin, caller!.Id, ct);
        if (!result.Success) return MapUploadError(result.ErrorMessage, result.ErrorCode);

        return Ok(new
        {
            uploadId = result.UploadId,
            fileName = result.FileName,
            fileSizeBytes = result.FileSizeBytes
        });
    }

    [HttpDelete("restore-upload/{uploadId}")]
    [Authorize(Policy = AppPermissions.Admin.PolicyName)]
    public async Task<IActionResult> CancelRestoreUpload(string uploadId)
    {
        var accessError = ValidateAdminBackupAccess("restore backups", out var caller);
        if (accessError != null) return accessError;
        if (_uploadService == null) return Ok();

        await _uploadService.CancelSessionAsync(uploadId, BackupUploadScope.Admin, caller!.Id);
        return Ok();
    }
}
