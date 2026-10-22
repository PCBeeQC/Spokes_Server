using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Migrates legacy Employee.OidcSub values into first-class OpenIdAccount records with LinkedEmployeeId set,
/// eliminating the need for runtime fallback scans across auth handlers.
/// Tracked by MigrationRunnerService and recorded in MigrationHistory.json so it executes exactly once.
/// </summary>
public class LegacyOidcSubMigrationStep : IMigrationStep
{
    private readonly EmployeeRepository _employees;
    private readonly OpenIdAccountRepository _openIdAccounts;
    private readonly ILogger<LegacyOidcSubMigrationStep>? _logger;

    public string MigrationKey => "20260107_LegacyOidcSubToOpenIdAccount";
    public string Description => "Migrates legacy Employee.OidcSub values into OpenIdAccount entities";
    public int Order => 70;

    public LegacyOidcSubMigrationStep(
        EmployeeRepository employees,
        OpenIdAccountRepository openIdAccounts,
        ILogger<LegacyOidcSubMigrationStep>? logger = null)
    {
        _employees = employees;
        _openIdAccounts = openIdAccounts;
        _logger = logger;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        int migrated = 0;

        foreach (var employee in _employees.GetAll())
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (string.IsNullOrEmpty(employee.MigratedOidcSub)) continue;

            var sub = employee.MigratedOidcSub;
            var account = _openIdAccounts.GetBySub(sub);

            if (account == null)
            {
                account = new OpenIdAccount
                {
                    Sub = sub,
                    LinkedEmployeeId = employee.Id,
                    Name = employee.FullName,
                    Email = employee.Email,
                    FirstSeenAt = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow
                };
                _openIdAccounts.Save(account);
            }
            else if (string.IsNullOrEmpty(account.LinkedEmployeeId))
            {
                account.LinkedEmployeeId = employee.Id;
                if (string.IsNullOrEmpty(account.Email) && !string.IsNullOrEmpty(employee.Email))
                {
                    account.Email = employee.Email;
                }
                _openIdAccounts.Save(account);
            }

            employee.MigratedOidcSub = null;
            _employees.Save(employee);
            migrated++;
        }

        if (migrated > 0)
        {
            _logger?.LogInformation("[LegacyOidcSubMigrationStep] Successfully migrated {Count} legacy Employee OidcSub records to OpenIdAccounts.", migrated);
        }

        return Task.CompletedTask;
    }
}
