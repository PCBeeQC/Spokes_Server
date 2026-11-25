using Microsoft.Extensions.Logging;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Security;

public sealed record UploadVerificationResult(
    bool IsSuccess,
    List<ApiFileUpload.UploadResult> VerifiedResults,
    string? ErrorMessage);

/// <summary>
/// Centralizes server-side verification of uploaded file completion callbacks from Blazor components.
/// Enforces active unsuspended user authorization, upload ACLs, DataProtection upload receipts,
/// physical file existence, and canonical path reconstruction.
/// </summary>
public sealed class UploadCompletionVerifier
{
    private readonly UserService _userService;
    private readonly IFileAuthorizationService _fileAuth;
    private readonly FileTokenService _fileTokenService;
    private readonly IFileService _fileService;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ILogger<UploadCompletionVerifier> _logger;

    public UploadCompletionVerifier(
        UserService userService,
        IFileAuthorizationService fileAuth,
        FileTokenService fileTokenService,
        IFileService fileService,
        SystemConfigRepository systemConfigs,
        ILogger<UploadCompletionVerifier> logger)
    {
        _userService = userService;
        _fileAuth = fileAuth;
        _fileTokenService = fileTokenService;
        _fileService = fileService;
        _systemConfigs = systemConfigs;
        _logger = logger;
    }

    public async Task<UploadVerificationResult> VerifyAndNormalizeAsync(
        IEnumerable<ApiFileUpload.UploadResult>? rawResults,
        string? expectedCategory,
        string? expectedContextId,
        string? uploadUrl = null)
    {
        var items = rawResults?.Where(r => r != null).ToList() ?? [];
        if (items.Count == 0)
        {
            return new UploadVerificationResult(false, [], "Upload completed but server returned no file data.");
        }

        if (string.IsNullOrWhiteSpace(expectedCategory) || string.IsNullOrWhiteSpace(expectedContextId))
        {
            return new UploadVerificationResult(false, [], "Upload context is invalid.");
        }

        var user = await _userService.GetEmployeeAsync();
        if (user == null || !user.IsActiveUnsuspended)
        {
            _logger.LogWarning("Rejected upload completion callback: caller is null, inactive, suspended, or banned.");
            return new UploadVerificationResult(false, [], "Upload permission denied.");
        }

        if (!_fileAuth.IsKnownCategory(expectedCategory) || !_fileAuth.CanUpload(expectedCategory, expectedContextId, user))
        {
            _logger.LogWarning(
                "Rejected upload completion callback: user {UserId} is not authorized to upload to {Category}/{ContextId}.",
                user.Id,
                expectedCategory,
                expectedContextId);
            return new UploadVerificationResult(false, [], "Upload permission denied.");
        }

        bool singleUseReceipt = expectedCategory.Equals("temp", StringComparison.OrdinalIgnoreCase);
        var verified = new List<ApiFileUpload.UploadResult>(items.Count);

        foreach (var item in items)
        {
            if (!_fileTokenService.TryValidateUploadReceiptToken(
                    item.UploadToken,
                    expectedCategory,
                    expectedContextId,
                    user.Id,
                    out var safeName,
                    consumeSingleUse: singleUseReceipt))
            {
                _logger.LogWarning(
                    "Rejected upload completion callback with invalid or missing upload receipt for user {UserId} in {Category}/{ContextId} (claimed path: {ClaimedPath}).",
                    user.Id,
                    expectedCategory,
                    expectedContextId,
                    item.FilePath);
                return new UploadVerificationResult(false, [], "Upload receipt verification failed.");
            }

            if (!_fileService.FileExists(expectedCategory, expectedContextId, safeName))
            {
                _logger.LogWarning(
                    "Rejected upload completion callback because verified file {SafeName} does not exist in {Category}/{ContextId}.",
                    safeName,
                    expectedCategory,
                    expectedContextId);
                return new UploadVerificationResult(false, [], "Uploaded file could not be verified on disk.");
            }

            // Never trust client-supplied FilePath, Category, ContextId, or raw FileName; reconstruct canonical values server-side.
            item.Category = expectedCategory;
            item.ContextId = expectedContextId;
            item.FilePath = $"/spokesapi/files/{expectedCategory}/{expectedContextId}/{safeName}";
            item.FileName = FileHelper.SanitizeDisplayFileName(item.FileName);
            verified.Add(item);
        }

        return new UploadVerificationResult(true, verified, null);
    }
}
