using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Services.Maintenance;

namespace Spokes_Server.Controllers;

[Route("setup/api")]
[ApiController]
[AllowAnonymous]
public class SetupApiController : SpokesControllerBase
{
    private readonly Database _db;
    private readonly BackupService _backupService;
    private readonly ILogger<SetupApiController> _logger;

    public SetupApiController(Database db, BackupService backupService, ILogger<SetupApiController> logger)
    {
        _db = db;
        _backupService = backupService;
        _logger = logger;
    }

    [HttpPost("restore-backup")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> RestoreBackup([FromForm] IFormFileCollection? files, CancellationToken ct)
    {
        var config = _db.SystemConfigs.Get();
        if (config.IsSetupComplete)
        {
            return ForbiddenError("Setup is already complete.", "SETUP_ALREADY_COMPLETE");
        }

        var file = files?.FirstOrDefault() ?? (Request.HasFormContentType ? Request.Form.Files.FirstOrDefault() : null);
        if (file == null || file.Length == 0)
        {
            return BadRequestError("No backup file provided.");
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"spokes_setup_restore_{Guid.NewGuid():N}_{Path.GetFileName(file.FileName)}");
        try
        {
            await using (var fs = System.IO.File.Create(tempFile))
            {
                await file.CopyToAsync(fs, ct);
            }

            var result = await _backupService.RestoreBackupAsync(tempFile, _logger, ct);
            if (result.Success)
            {
                return Ok(new[]
                {
                    new ApiFileUpload.UploadResult
                    {
                        FileName = file.FileName,
                        FilePath = "/setup/restored",
                        FileSizeBytes = file.Length
                    }
                });
            }

            return BadRequestError(result.ErrorMessage ?? "Failed to restore backup archive.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup restore backup failed.");
            return BadRequestError(ex.Message);
        }
        finally
        {
            try
            {
                if (System.IO.File.Exists(tempFile))
                {
                    System.IO.File.Delete(tempFile);
                }
            }
            catch { }
        }
    }
}
