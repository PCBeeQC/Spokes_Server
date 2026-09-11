using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Constants;

namespace Spokes_Server.Controllers;

[Route("spokesapi/backups")]
[ApiController]
[Authorize]
public class BackupController : SpokesControllerBase
{
    private readonly BackupService _backupService;
    private readonly UserService _userService;

    public BackupController(BackupService backupService, UserService userService)
    {
        _backupService = backupService;
        _userService = userService;
    }

    [HttpGet("{fileName}")]
    public IActionResult DownloadBackup(string fileName)
    {
        var config = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        if (config.GetValue<bool>("Spokes_DemoMode"))
        {
            return Forbid();
        }
        // 1. Identify User
        var user = _userService.GetEmployee(User);

        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) 
        {
            return Unauthorized("User profile not active or not found");
        }

        // 2. Check Permissions (Must be Admin or have ManageSettings permission)
        if (!user.IsAdmin && !user.HasPermission(AppPermissions.Admin.ManageSettings))
        {
            return Forbid();
        }

        // 3. Validate Filename
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\"))
        {
            return BadRequest("Invalid file name");
        }

        // 4. Find the backup file
        var backups = _backupService.GetBackups();
        var backup = backups.FirstOrDefault(b => b.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        if (backup == null || !System.IO.File.Exists(backup.FilePath))
        {
            return NotFound();
        }

        // 5. Serve the file directly using PhysicalFileResult
        return PhysicalFile(backup.FilePath, "application/zip", backup.FileName, enableRangeProcessing: true);
    }
}
