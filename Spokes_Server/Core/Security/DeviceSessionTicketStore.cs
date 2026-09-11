namespace Spokes_Server.Core.Security;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate;

/// <summary>
/// Server-side authentication ticket store backed by DeviceSession JSON repository.
/// Replaces the stateless encrypted cookie with a stable session reference key.
/// 
/// When configured as options.SessionStore, ASP.NET Core's CookieAuthenticationHandler
/// stores only the session key in the browser cookie. The full AuthenticationTicket
/// lives here, looked up by key on each request.
/// 
/// Benefits:
/// - Cookie value is tiny and NEVER changes (stable key = no sync issues on mobile)
/// - Sliding expiration updates server-side ticket only (cookie stays the same)
/// - Session revocation allows ticket retrieval so OnValidatePrincipal flags IsRevoked and downstream middleware triggers OIDC signout
/// - Tickets survive server restarts (persisted to disk via JsonRepository)
/// </summary>
public class DeviceSessionTicketStore : ITicketStore
{
    private const string KeyPrefix = "session-";
    private readonly IServiceProvider _serviceProvider;

    public DeviceSessionTicketStore(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Stores a new authentication ticket. Called during SignInAsync().
    /// Extracts the SessionId claim from the ticket to link to the DeviceSession.
    /// Returns the session key that will be encrypted into the browser cookie.
    /// </summary>
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var sessionId = ticket.Principal.FindFirst("SessionId")?.Value;
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new InvalidOperationException("Identity missing in session store");
        }

        var key = KeyPrefix + sessionId;
        StoreTicketData(key, ticket);
        return Task.FromResult(key);
    }

    /// <summary>
    /// Updates the ticket for an existing session. Called during sliding expiration renewal.
    /// The cookie value (key) stays the same — only the server-side ticket data is updated.
    /// </summary>
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        StoreTicketData(key, ticket);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Retrieves the ticket for a session key. Called on every authenticated request.
    /// Returns null if the session is not found, expired, or has no ticket data.
    /// Note: Revoked sessions deliberately return the ticket so OnValidatePrincipal can set IsRevoked
    /// and trigger OIDC signout in the downstream revocation middleware.
    /// </summary>
    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var sessionId = ExtractSessionId(key);
        if (string.IsNullOrEmpty(sessionId))
            return Task.FromResult<AuthenticationTicket?>(null);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();

        var session = db.DeviceSessions.GetById(sessionId);
        if (session == null || session.ExpiresAt <= DateTime.UtcNow)
            return Task.FromResult<AuthenticationTicket?>(null);

        if (string.IsNullOrEmpty(session.TicketData))
            return Task.FromResult<AuthenticationTicket?>(null);

        try
        {
            var ticketBytes = Convert.FromBase64String(session.TicketData);
            var ticket = TicketSerializer.Default.Deserialize(ticketBytes);
            return Task.FromResult(ticket);
        }
        catch
        {
            // Corrupted ticket data — treat as unauthenticated
            return Task.FromResult<AuthenticationTicket?>(null);
        }
    }

    /// <summary>
    /// Removes ticket data from a session. Called during SignOutAsync().
    /// Does NOT revoke the session — only clears the cached ticket.
    /// </summary>
    public Task RemoveAsync(string key)
    {
        var sessionId = ExtractSessionId(key);
        if (string.IsNullOrEmpty(sessionId))
            return Task.CompletedTask;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();

        var session = db.DeviceSessions.GetById(sessionId);
        if (session != null)
        {
            session.TicketData = null;
            db.DeviceSessions.Save(session);
        }
        return Task.CompletedTask;
    }

    private void StoreTicketData(string key, AuthenticationTicket ticket)
    {
        var sessionId = ExtractSessionId(key);
        if (string.IsNullOrEmpty(sessionId))
            return;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();

        var session = db.DeviceSessions.GetById(sessionId);
        if (session != null)
        {
            var ticketBytes = TicketSerializer.Default.Serialize(ticket);
            session.TicketData = Convert.ToBase64String(ticketBytes);
            db.DeviceSessions.Save(session);
        }
    }

    private static string? ExtractSessionId(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        if (key.StartsWith(KeyPrefix))
            return key[KeyPrefix.Length..];
        return key;
    }
}
