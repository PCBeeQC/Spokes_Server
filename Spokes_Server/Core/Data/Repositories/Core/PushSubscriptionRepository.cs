using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Data.Repositories.Core;

/// <summary>
/// Repository for managing Web Push subscriptions.
/// </summary>
public class PushSubscriptionRepository : JsonRepository<PushSubscription>
{
    public PushSubscriptionRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "data", "pushsubscriptions"))
    {
    }

    protected override string GetFilePath(PushSubscription entity)
    {
        return Path.Combine(_basePath, $"{entity.Id}.json");
    }

    /// <summary>
    /// Get all push subscriptions for a specific user.
    /// </summary>
    public List<PushSubscription> GetByUserId(string userId)
    {
        return GetAll().Where(s => s.UserId == userId).ToList();
    }

    public Task<List<PushSubscription>> GetByUserIdAsync(string userId) => Task.FromResult(GetByUserId(userId));

    /// <summary>
    /// Get a subscription by its endpoint URL.
    /// </summary>
    public PushSubscription? GetByEndpoint(string endpoint)
    {
        return GetAll().FirstOrDefault(s => s.Endpoint == endpoint);
    }

    public Task<PushSubscription?> GetByEndpointAsync(string endpoint) => Task.FromResult(GetByEndpoint(endpoint));

    /// <summary>
    /// Delete a subscription by its endpoint URL.
    /// </summary>
    public bool DeleteByEndpoint(string endpoint)
    {
        var subscription = GetByEndpoint(endpoint);
        if (subscription != null)
        {
            Delete(subscription.Id);
            return true;
        }
        return false;
    }

    public Task<bool> DeleteByEndpointAsync(string endpoint) => Task.FromResult(DeleteByEndpoint(endpoint));

    /// <summary>
    /// Get all enabled push subscriptions for a user, optionally filtered by device type.
    /// </summary>
    public List<PushSubscription> GetEnabledByUserId(string userId, string? deviceType = null)
    {
        var query = GetAll().Where(s => s.UserId == userId && s.IsEnabled);
        if (!string.IsNullOrEmpty(deviceType))
        {
            query = query.Where(s => s.DeviceType.Equals(deviceType, StringComparison.OrdinalIgnoreCase));
        }
        return query.ToList();
    }

    public Task<List<PushSubscription>> GetEnabledByUserIdAsync(string userId, string? deviceType = null) => Task.FromResult(GetEnabledByUserId(userId, deviceType));

    /// <summary>
    /// Delete all subscriptions for a user.
    /// </summary>
    public void DeleteByUserId(string userId)
    {
        var subscriptions = GetByUserId(userId);
        foreach (var sub in subscriptions)
        {
            Delete(sub.Id);
        }
    }

    public Task DeleteByUserIdAsync(string userId)
    {
        DeleteByUserId(userId);
        return Task.CompletedTask;
    }
}


