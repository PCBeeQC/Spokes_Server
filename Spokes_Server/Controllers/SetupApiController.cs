using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Services.Maintenance;

namespace Spokes_Server.Controllers;

[Route("setup/api")]
[ApiController]
[AllowAnonymous]
[Spokes_Server.Core.Security.AllowDuringSetup]
public class SetupApiController : SpokesControllerBase
{
    private readonly Database _db;
    private readonly BackupRestoreUploadService _uploadService;
    private readonly ILogger<SetupApiController> _logger;

    public SetupApiController(
        Database db,
        BackupRestoreUploadService uploadService,
        ILogger<SetupApiController> logger)
    {
        _db = db;
        _uploadService = uploadService;
        _logger = logger;
    }

    private IActionResult? EnsureSetupIncomplete()
    {
        var config = _db.SystemConfigs.Get();
        if (config.IsSetupComplete)
        {
            return ForbiddenError("Setup is already complete.", "SETUP_ALREADY_COMPLETE");
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

    [HttpPost("restore-upload/init")]
    public IActionResult InitRestoreUpload([FromBody] BackupUploadInitRequest? request)
    {
        var setupError = EnsureSetupIncomplete();
        if (setupError != null) return setupError;
        if (request == null) return BadRequestError("Upload initialization payload is required.");

        var result = _uploadService.InitSession(request.FileName, request.TotalBytes, request.ChunkSizeBytes, BackupUploadScope.Setup, null);
        if (!result.Success) return MapUploadError(result.ErrorMessage, result.ErrorCode);

        return Ok(new
        {
            uploadId = result.UploadId,
            chunkSizeBytes = result.ChunkSizeBytes,
            totalChunks = result.TotalChunks
        });
    }

    [HttpPut("restore-upload/{uploadId}/chunks/{chunkIndex:int}")]
    [RequestSizeLimit(BackupRestoreUploadService.MaxChunkSizeBytes + 1024 * 1024)]
    public async Task<IActionResult> UploadRestoreChunk(string uploadId, int chunkIndex, CancellationToken ct)
    {
        var setupError = EnsureSetupIncomplete();
        if (setupError != null) return setupError;

        var result = await _uploadService.AppendChunkAsync(
            uploadId,
            chunkIndex,
            Request.Body,
            Request.ContentLength,
            BackupUploadScope.Setup,
            null,
            ct);

        if (!result.Success)
        {
            _logger.LogWarning("Setup chunked restore upload {UploadId} chunk {ChunkIndex} rejected: {Error}", uploadId, chunkIndex, result.ErrorMessage);
            return MapUploadError(result.ErrorMessage, result.ErrorCode);
        }

        return Ok(new
        {
            chunkIndex = result.ChunkIndex,
            bytesReceived = result.BytesReceived,
            expectedTotalBytes = result.ExpectedTotalBytes
        });
    }

    [HttpPost("restore-upload/{uploadId}/complete")]
    public async Task<IActionResult> CompleteRestoreUpload(string uploadId, CancellationToken ct)
    {
        var setupError = EnsureSetupIncomplete();
        if (setupError != null) return setupError;

        var result = await _uploadService.CompleteSessionAsync(uploadId, BackupUploadScope.Setup, null, ct);
        if (!result.Success) return MapUploadError(result.ErrorMessage, result.ErrorCode);

        return Ok(new
        {
            uploadId = result.UploadId,
            fileName = result.FileName,
            fileSizeBytes = result.FileSizeBytes
        });
    }

    [HttpDelete("restore-upload/{uploadId}")]
    public async Task<IActionResult> CancelRestoreUpload(string uploadId)
    {
        var setupError = EnsureSetupIncomplete();
        if (setupError != null) return setupError;

        await _uploadService.CancelSessionAsync(uploadId, BackupUploadScope.Setup, null);
        return Ok();
    }
}
