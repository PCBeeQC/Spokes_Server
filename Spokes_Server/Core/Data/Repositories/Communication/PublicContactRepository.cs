using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class PublicContactRepository : JsonRepository<ContactPerson>
{
    public PublicContactRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "PublicContacts"), "*.json")
    {
    }

    protected override string GetFilePath(ContactPerson item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
