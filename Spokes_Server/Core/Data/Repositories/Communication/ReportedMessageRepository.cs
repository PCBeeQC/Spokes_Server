using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class ReportedMessageRepository : JsonRepository<ReportedMessage>
{
    public ReportedMessageRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Communication", "ReportedMessages"))
    {
    }

    protected override string GetFilePath(ReportedMessage item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
