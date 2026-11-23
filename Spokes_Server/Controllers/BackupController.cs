using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Core.Constants;
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

    public BackupController(BackupService backupService, IConfiguration config, UserService? userService = null)
    {
        _backupService = backupService;
        _config = config;
        _userService = userService;
    }

    [HttpGet("{fileName}")]
    [Authorize(Policy = AppPermissions.Admin.PolicyName)]
    public IActionResult DownloadBackup(string fileName)
    {
        var caller = _userService?.GetEmployee(User);
        if (caller is not { IsActiveUnsuspended: true } || !caller.IsEffectiveAdmin)
        {
            return ForbiddenError("Administrator access is required to download backups.", "ADMIN_REQUIRED");
        }

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
