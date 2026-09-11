using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Core;

public class AvatarMigrationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AvatarMigrationService> _logger;
    private readonly IConfiguration _config;

    public AvatarMigrationService(IServiceProvider serviceProvider, ILogger<AvatarMigrationService> logger, IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[AvatarMigrationService] Starting avatar migration background task...");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var employeeRepo = scope.ServiceProvider.GetRequiredService<EmployeeRepository>();

            var allEmployees = employeeRepo.GetAll();
            int migratedCount = 0;
            int failedCount = 0;

            foreach (var employee in allEmployees)
            {
                if (stoppingToken.IsCancellationRequested) break;

                // Check if there is an unmigrated Base64 avatar
                if (!string.IsNullOrEmpty(employee.AvatarBase64) && employee.AvatarBase64.Contains(","))
                {
                    try
                    {
                        var base64Data = employee.AvatarBase64.Substring(employee.AvatarBase64.IndexOf(",") + 1);
                        var bytes = Convert.FromBase64String(base64Data);
                        
                        var dataPath = _config["DataPath"] ?? "Data";
                        var empPath = Path.Combine(dataPath, "Employees", employee.Id);
                        
                        if (!Directory.Exists(empPath))
                        {
                            Directory.CreateDirectory(empPath);
                        }

                        var filePath = Path.Combine(empPath, "avatar.png");
                        await File.WriteAllBytesAsync(filePath, bytes, stoppingToken);

                        employee.AvatarFile = "avatar.png";
                        employee.AvatarBase64 = null;
                        employee.AvatarVersion++;

                        employeeRepo.Save(employee);
                        migratedCount++;
                        
                        _logger.LogInformation($"[AvatarMigrationService] Successfully migrated avatar for employee {employee.Id}");
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        _logger.LogError(ex, $"[AvatarMigrationService] Failed to migrate avatar for employee {employee.Id}");
                    }
                }
            }

            _logger.LogInformation($"[AvatarMigrationService] Migration completed. Migrated: {migratedCount}, Failed: {failedCount}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AvatarMigrationService] A fatal error occurred during avatar migration.");
        }
    }
}
