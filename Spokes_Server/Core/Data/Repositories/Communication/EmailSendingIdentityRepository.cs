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

public class EmailSendingIdentityRepository : JsonRepository<EmailSendingIdentity>
{
    public EmailSendingIdentityRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public EmailSendingIdentityRepository(DiskPersistenceService writer, IConfiguration config, ILogger<EmailSendingIdentityRepository>? logger)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "EmailSendingIdentities"), "*.json", logger)
    {
    }

    protected override string GetFilePath(EmailSendingIdentity item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public virtual List<EmailSendingIdentity> GetAccessibleForUser(string employeeId, IEnumerable<string>? userTeamIds = null) =>
        GetAll()
            .Where(id => id.CanUserAccess(employeeId, userTeamIds))
            .OrderBy(id => id.DisplayName)
            .ThenBy(id => id.EmailAddress)
            .ToList();
}
