namespace Spokes_Server.Core.Models.Core;

using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




/// <summary>
/// Represents an OpenID Connect authentication identity.
/// Multiple OpenIdAccounts can be linked to a single Employee.
/// One OpenIdAccount can only link to one Employee at a time.
/// </summary>
public class OpenIdAccount : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (OpenIdAccount)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    /// <summary>
    /// The OpenID Connect 'sub' (subject) claim - unique identifier from the IdP.
    /// </summary>
    public string Sub { get; set; } = string.Empty;

    /// <summary>
    /// Display name from the identity provider.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Email address from the identity provider.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The Spokes Employee ID this account is linked to. Empty if unlinked.
    /// </summary>
    public string LinkedEmployeeId { get; set; } = string.Empty;

    /// <summary>
    /// When this OpenID account was first seen (first login attempt).
    /// </summary>
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this OpenID account last successfully logged in.
    /// </summary>
    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Helper to check if this account is currently linked to an employee.
    /// </summary>
    public bool IsLinked => !string.IsNullOrEmpty(LinkedEmployeeId);
}



