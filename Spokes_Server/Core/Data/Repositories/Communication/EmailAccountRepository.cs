using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class EmailAccountRepository : JsonRepository<EmailAccount>
{
    public EmailAccountRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public EmailAccountRepository(DiskPersistenceService writer, IConfiguration config, ILogger<EmailAccountRepository>? logger)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "EmailAccounts"), "Account.json", logger)
    {
    }

    protected override string GetFilePath(EmailAccount item) =>
        Path.Combine(_basePath, item.Id, "Account.json");

    public virtual List<EmailAccount> GetByOwner(string employeeId) =>
        GetAll().Where(a => string.Equals(a.OwnerEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)).ToList();

    public virtual List<EmailAccount> GetAccessibleForUser(string employeeId, IEnumerable<string>? userTeamIds = null, bool includeArchived = false) =>
        GetAll()
            .Where(a => (includeArchived || !a.IsArchived) && a.CanUserAccess(employeeId, userTeamIds))
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Name)
            .ThenBy(a => a.EmailAddress)
            .ToList();

    public virtual List<EmailAccount> GetArchivedAccessibleForUser(string employeeId, IEnumerable<string>? userTeamIds = null) =>
        GetAll()
            .Where(a => a.IsArchived && a.CanUserAccess(employeeId, userTeamIds))
            .OrderByDescending(a => a.ArchivedAt)
            .ThenBy(a => a.SortOrder)
            .ThenBy(a => a.Name)
            .ThenBy(a => a.EmailAddress)
            .ToList();
}
