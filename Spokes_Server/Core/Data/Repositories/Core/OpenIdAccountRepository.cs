using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class OpenIdAccountRepository : JsonRepository<OpenIdAccount>
{
    public OpenIdAccountRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "OpenIdAccounts"))
    {
    }

    /// <summary>
    /// Find an OpenID account by its OIDC subject claim.
    /// </summary>
    public OpenIdAccount? GetBySub(string sub) =>
        _cache.Values.FirstOrDefault(a => a.Sub == sub);

    /// <summary>
    /// Get all OpenID accounts linked to a specific employee.
    /// </summary>
    public List<OpenIdAccount> GetByEmployeeId(string employeeId) =>
        _cache.Values.Where(a => a.LinkedEmployeeId == employeeId).ToList();

    /// <summary>
    /// Get all OpenID accounts that are not linked to any employee.
    /// </summary>
    public List<OpenIdAccount> GetUnlinked() =>
        _cache.Values.Where(a => string.IsNullOrEmpty(a.LinkedEmployeeId)).ToList();

    protected override string GetFilePath(OpenIdAccount item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
