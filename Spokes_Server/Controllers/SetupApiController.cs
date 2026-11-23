using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Services.Maintenance;

namespace Spokes_Server.Controllers;

[Route("setup/api")]
[ApiController]
[AllowAnonymous]
[Spokes_Server.Core.Security.AllowDuringSetup]
public class SetupApiController : SpokesControllerBase
{
    private readonly Database _db;
    private readonly BackupService _backupService;
    private readonly ILogger<SetupApiController> _logger;
    private readonly IConfiguration? _config;
    private readonly IBackupEncryptionService? _encryptionService;

    public SetupApiController(
        Database db,
        BackupService backupService,
        ILogger<SetupApiController> logger,
        IConfiguration? config = null,
        IBackupEncryptionService? encryptionService = null)
    {
        _db = db;
        _backupService = backupService;
        _logger = logger;
        _config = config;
        _encryptionService = encryptionService;
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

        var configuredMasterPassword = _config?["SPOKES_MASTER_PASSWORD"]
            ?? Environment.GetEnvironmentVariable("SPOKES_MASTER_PASSWORD");

        // Pre-validate encrypted backup password match before saving or extracting the archive on disk
        bool verifiedOnUploadStream = false;
        if (_encryptionService != null)
        {
            await using var uploadInspectionStream = file.OpenReadStream();
            if (uploadInspectionStream.CanSeek)
            {
                var preValidationError = ValidateEncryptedBackupStream(uploadInspectionStream, configuredMasterPassword);
                if (preValidationError != null)
                {
                    return preValidationError;
                }
                verifiedOnUploadStream = true;
            }
        }

        var dataPath = _config?["DataPath"] ?? "Data";
        var stagingDir = !string.IsNullOrWhiteSpace(dataPath)
            ? Path.Combine(dataPath, "Backups", ".staging")
            : Path.GetTempPath();
        Directory.CreateDirectory(stagingDir);

        var tempFile = Path.Combine(stagingDir, $"spokes_setup_restore_{Guid.NewGuid():N}_{Path.GetFileName(file.FileName)}");
        try
        {
            await using (var fs = System.IO.File.Create(tempFile))
            {
                await file.CopyToAsync(fs, ct);
            }

            if (_encryptionService != null && !verifiedOnUploadStream)
            {
                await using var fileVerifyStream = System.IO.File.OpenRead(tempFile);
                var validationError = ValidateEncryptedBackupStream(fileVerifyStream, configuredMasterPassword);
                if (validationError != null)
                {
                    return validationError;
                }
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

    private IActionResult? ValidateEncryptedBackupStream(Stream stream, string? configuredMasterPassword)
    {
        if (_encryptionService == null) return null;

        bool isEncrypted = _encryptionService.IsEncryptedArchive(stream);
        stream.Position = 0;
        if (!isEncrypted) return null;

        if (string.IsNullOrWhiteSpace(configuredMasterPassword))
        {
            return BadRequestError("Backup archive is encrypted, but SPOKES_MASTER_PASSWORD is not configured on this server.");
        }

        if (!_encryptionService.VerifyMasterPassword(stream, configuredMasterPassword))
        {
            return BadRequestError("Backup master password does not match SPOKES_MASTER_PASSWORD.");
        }

        return null;
    }
}
