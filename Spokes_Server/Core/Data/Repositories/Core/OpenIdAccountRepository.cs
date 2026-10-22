using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class OpenIdAccountRepository : JsonRepository<OpenIdAccount>
{
    public OpenIdAccountRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "OpenIdAccounts"))
    {
    }

    public OpenIdAccount? GetBySub(string sub)
    {
        EnsureLoaded();
        return _cache.Values.FirstOrDefault(a => a.Sub == sub);
    }

    public List<OpenIdAccount> GetByEmployeeId(string employeeId)
    {
        EnsureLoaded();
        return _cache.Values.Where(a => a.LinkedEmployeeId == employeeId).ToList();
    }

    public List<OpenIdAccount> GetUnlinked()
    {
        EnsureLoaded();
        return _cache.Values.Where(a => string.IsNullOrEmpty(a.LinkedEmployeeId)).ToList();
    }

    protected override string GetFilePath(OpenIdAccount item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
