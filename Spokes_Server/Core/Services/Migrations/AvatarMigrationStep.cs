using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Core.Services.Migrations;

public class AvatarMigrationStep : IMigrationStep
{
    private readonly EmployeeRepository _employeeRepo;
    private readonly string _dataPath;
    private readonly ILogger? _logger;

    public string MigrationKey => "20260103_AvatarFileCentric";
    public string Description => "Migrates inline Base64 employee avatars to disk files";
    public int Order => 30;

    public AvatarMigrationStep(EmployeeRepository employeeRepo, IConfiguration config, ILogger? logger = null)
    {
        _employeeRepo = employeeRepo;
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var allEmployees = _employeeRepo.GetAll();
        int migratedCount = 0;
        int failedCount = 0;

        foreach (var employee in allEmployees)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (!string.IsNullOrEmpty(employee.AvatarBase64))
            {
                try
                {
                    if (!DataUriHelper.TryParse(employee.AvatarBase64, out var bytes, out _))
                    {
                        throw new FormatException($"Invalid or corrupt base64 avatar data for employee {employee.Id}");
                    }
                    var empPath = Path.Combine(_dataPath, "Employees", employee.Id);
                    if (!Directory.Exists(empPath))
                    {
                        Directory.CreateDirectory(empPath);
                    }

                    var filePath = Path.Combine(empPath, "avatar.png");
                    await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);

                    employee.AvatarFile = "avatar.png";
                    employee.AvatarBase64 = null;
                    employee.AvatarVersion++;

                    _employeeRepo.Save(employee);
                    migratedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger?.LogError(ex, "Failed to migrate avatar for employee {EmployeeId}", employee.Id);
                }
            }
        }

        _logger?.LogInformation("[AvatarMigrationStep] Completed avatar migration. Migrated: {MigratedCount}, Failed: {FailedCount}.", migratedCount, failedCount);
    }
}
