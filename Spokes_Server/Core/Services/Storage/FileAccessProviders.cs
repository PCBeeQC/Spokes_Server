namespace Spokes_Server.Core.Services.Storage;

using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;

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

        return user!.HasEffectivePermission(permission, allowSuspended: true);
    }
}

public class AvatarFileAccessProvider : IFileAccessProvider
{
    private readonly EmployeeRepository? _employees;
    private readonly IUserPrivacyService? _privacyService;
    private readonly VoiceStateService? _voiceState;

    public AvatarFileAccessProvider(
        EmployeeRepository? employees = null,
        IUserPrivacyService? privacyService = null,
        VoiceStateService? voiceState = null)
    {
        _employees = employees;
        _privacyService = privacyService;
        _voiceState = voiceState;
    }

    public string Category => "avatars";

    // Avatars are readable by active, non-banned users who can discover the target user
    // or when the target user is an active participant in a voice channel.
    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user)) return false;
        if (user.IsEffectiveAdmin || string.Equals(user.Id, contextId, StringComparison.Ordinal)) return true;
        if (_employees == null || _privacyService == null) return true;

        var target = _employees.GetById(contextId);
        if (target == null) return false;
        if (target.IsSystem || _privacyService.CanDiscoverUser(user, target)) return true;
        return _voiceState != null && _voiceState.IsUserInAnyVoiceChannel(target.Id);
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user)) return false;
        return user.IsAdmin || user.Id == contextId;
    }
}

public class SignatureFileAccessProvider : IFileAccessProvider
{
    private readonly ProjectDocumentRepository? _projectDocuments;
    private readonly StandardDocumentRepository? _standardDocuments;
    private readonly ProjectRepository? _projects;
    private readonly TeamRepository? _teams;

    public SignatureFileAccessProvider(
        ProjectDocumentRepository? projectDocuments = null,
        StandardDocumentRepository? standardDocuments = null,
        ProjectRepository? projects = null,
        TeamRepository? teams = null)
    {
        _projectDocuments = projectDocuments;
        _standardDocuments = standardDocuments;
        _projects = projects;
        _teams = teams;
    }

    public string Category => "signatures";

    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user)) return false;
        if (user.IsAdmin || string.Equals(user.Id, contextId, StringComparison.Ordinal)) return true;

        bool canViewBusinessDocs =
            FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.BusinessDocuments.View) ||
            FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Business.ManageSettings);

        if (string.Equals(contextId, "business", StringComparison.OrdinalIgnoreCase))
        {
            return canViewBusinessDocs;
        }

        // Legacy fallback: allow read only if an already-persisted document explicitly embeds this signature URL
        // and the caller is authorized to view that specific document/project.
        string prefix = $"/spokesapi/files/signatures/{contextId}/";

        if (_standardDocuments != null && canViewBusinessDocs)
        {
            foreach (var doc in _standardDocuments.GetAll())
            {
                if ((!string.IsNullOrEmpty(doc.Body) && doc.Body.Contains(prefix, StringComparison.OrdinalIgnoreCase)) ||
                    doc.Blocks.Any(b => b.Properties.Values.Any(v => v != null && v.Contains(prefix, StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }
            }
        }

        if (_projectDocuments != null)
        {
            foreach (var doc in _projectDocuments.GetAll())
            {
                bool referencesSignature =
                    (!string.IsNullOrEmpty(doc.ResolvedBody) && doc.ResolvedBody.Contains(prefix, StringComparison.OrdinalIgnoreCase)) ||
                    doc.Blocks.Any(b => b.Properties.Values.Any(v => v != null && v.Contains(prefix, StringComparison.OrdinalIgnoreCase)));

                if (!referencesSignature) continue;

                if (string.IsNullOrWhiteSpace(doc.ProjectId))
                {
                    if (canViewBusinessDocs) return true;
                }
                else if (_projects != null && _teams != null &&
                         ProjectFileAccessProvider.CanReadProject(_projects, _teams, user, doc.ProjectId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user)) return false;
        if (user.IsAdmin || string.Equals(user.Id, contextId, StringComparison.Ordinal)) return true;
        if (string.Equals(contextId, "business", StringComparison.OrdinalIgnoreCase))
        {
            return user.HasEffectivePermission(AppPermissions.BusinessDocuments.View) ||
                   user.HasEffectivePermission(AppPermissions.Business.ManageSettings);
        }
        return false;
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
