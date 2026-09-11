namespace Spokes_Server.Core.Data.Repositories.Communication;

using Spokes_Server.Core.Models.Communication;

using Microsoft.Extensions.Configuration;
using System.IO;

public class ReportedMessageRepository : JsonRepository<ReportedMessage>
{
    public ReportedMessageRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Communication", "ReportedMessages"))
    {
    }

    protected override string GetFilePath(ReportedMessage item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }
}
