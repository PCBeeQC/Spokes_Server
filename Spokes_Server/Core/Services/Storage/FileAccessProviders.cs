namespace Spokes_Server.Core.Services.Storage;

using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.HR;

public interface IFileAccessProvider
{
    string Category { get; }
    bool CanAccess(Employee user, string contextId);
    bool CanUpload(Employee user, string contextId) => CanAccess(user, contextId);
}

public class AvatarFileAccessProvider : IFileAccessProvider
{
    public string Category => "avatars";

    // Avatars are public to all authenticated active users
    public bool CanAccess(Employee user, string contextId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;
        return true;
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;
        return user.IsAdmin || user.Id == contextId;
    }
}

public class SignatureFileAccessProvider : IFileAccessProvider
{
    public string Category => "signatures";

    // Signatures. We just align with the private use requirement
    public bool CanAccess(Employee user, string contextId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;
        if (user.IsAdmin || user.Id == contextId) return true;
        // Allow if user has finance/accounting permissions for generating documents
        return user.HasPermission(AppPermissions.Finance.AccessAccounting);
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;
        return user.IsAdmin || user.Id == contextId;
    }
}

public class TempFileAccessProvider : IFileAccessProvider
{
    public string Category => "temp";

    public bool CanAccess(Employee user, string contextId)
    {
        // Any active, non-suspended, non-banned authenticated employee can use temp staging
        return user is { IsActive: true, IsSuspended: false, IsBanned: false };
    }
}
