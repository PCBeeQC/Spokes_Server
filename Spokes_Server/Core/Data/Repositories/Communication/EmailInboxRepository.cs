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

public class EmailInboxRepository : JsonRepository<EmailInbox>
{
    public EmailInboxRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public EmailInboxRepository(DiskPersistenceService writer, IConfiguration config, ILogger<EmailInboxRepository>? logger)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "EmailInboxes"), "*.json", logger)
    {
    }

    protected override string GetFilePath(EmailInbox item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public virtual List<EmailInbox> GetByAccount(string accountId) =>
        GetAll()
            .Where(i => string.Equals(i.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Name)
            .ToList();

    public virtual List<EmailInbox> GetAccessibleForUser(string employeeId, IEnumerable<string>? userTeamIds = null) =>
        GetAll()
            .Where(i => i.CanUserAccess(employeeId, userTeamIds))
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Name)
            .ToList();
}
