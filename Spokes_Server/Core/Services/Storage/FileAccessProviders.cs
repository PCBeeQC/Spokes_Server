namespace Spokes_Server.Core.Services.Storage;

using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.HR;

public interface IFileAccessProvider
{
    string Category { get; }
    bool CanAccess(Employee user, string contextId);
    bool CanUpload(Employee user, string contextId);
}

internal static class FileAccessPermissionHelper
{
    public static bool IsReadableUser(Employee? user)
        => user is { IsActive: true, IsBanned: false };

    public static bool IsWritableUser(Employee? user)
        => user is { IsActive: true, IsSuspended: false, IsBanned: false };

    /// <summary>
    /// Checks whether an active, non-banned user (including a read-only IsSuspended user)
    /// holds a specific permission directly, via PermissionGroups, or via IsAdmin.
    /// </summary>
    public static bool HasAssignedPermission(Employee? user, string permission)
    {
        if (!IsReadableUser(user))
        {
            return false;
        }

        if (user!.IsAdmin)
        {
            return true;
        }

        if (user.PermissionGroupIds.Count > 0 &&
            user.PermissionGroups.Any(g => g != null && g.Permissions.Contains(permission)))
        {
            return true;
        }

        return user.Permissions.Contains(permission);
    }
}

public class AvatarFileAccessProvider : IFileAccessProvider
{
    public string Category => "avatars";

    // Avatars are readable by all authenticated active (including read-only suspended) non-banned users
    public bool CanAccess(Employee user, string contextId)
    {
        return FileAccessPermissionHelper.IsReadableUser(user);
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user)) return false;
        return user.IsAdmin || user.Id == contextId;
    }
}

public class SignatureFileAccessProvider : IFileAccessProvider
{
    public string Category => "signatures";

    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user)) return false;
        if (user.IsAdmin || user.Id == contextId) return true;
        // Allow if user has finance/accounting permissions for viewing/generating documents
        return FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Finance.AccessAccounting);
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user)) return false;
        return user.IsAdmin || user.Id == contextId;
    }
}

public class TempFileAccessProvider : IFileAccessProvider
{
    public string Category => "temp";

    // Temporary staging files are write-only for internal ingestion and can never be read via file access
    public bool CanAccess(Employee user, string contextId) => false;

    public bool CanUpload(Employee user, string contextId)
    {
        return FileAccessPermissionHelper.IsWritableUser(user) &&
               (user.IsAdmin
                || user.HasPermission(AppPermissions.Admin.ManageSettings)
                || user.HasPermission(AppPermissions.Projects.Create)
                || user.HasPermission(AppPermissions.Projects.Edit)
                || user.HasPermission(AppPermissions.BusinessDocuments.View));
    }
}
