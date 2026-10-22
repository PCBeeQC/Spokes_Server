using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Services.Maintenance;

namespace Spokes_Server.Controllers;

[Route("spokesapi/backups")]
[ApiController]
[Authorize]
public class BackupController : SpokesControllerBase
{
    private readonly BackupService _backupService;
    private readonly IConfiguration _config;

    public BackupController(BackupService backupService, IConfiguration config)
    {
        _backupService = backupService;
        _config = config;
    }

    [HttpGet("{fileName}")]
    [Authorize(Policy = AppPermissions.Admin.ManageSettings)]
    public IActionResult DownloadBackup(string fileName)
    {
        if (_config.GetValue<bool>("Spokes_DemoMode"))
        {
            return ForbiddenError("Backups cannot be downloaded in demo mode.", "DEMO_MODE_FORBIDDEN");
        }

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
}
