using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Filters;

/// <summary>
/// Enforces upload authorization, demo-mode restrictions, disk-health gates, Content-Length
/// caps, and runtime-configured FormOptions BEFORE ASP.NET Core binds/buffers [FromForm]
/// multipart streams to temp disk. Runs at Order = 1000 (after DisableRequestSizeLimit = 900).
/// </summary>
public sealed class UploadRequestGuardFilter : IAsyncResourceFilter, IOrderedFilter
{
    private const long MultipartFramingOverheadBytes = 4L * 1024 * 1024;
    private const int MaxMultipartValueCount = 128;

    private readonly UserService _userService;
    private readonly IFileAuthorizationService _fileAuthService;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly FileTokenService? _fileTokenService;
    private readonly IStorageHealthService? _storageHealth;
    private readonly IConfiguration? _config;

    public UploadRequestGuardFilter(
        UserService userService,
        IFileAuthorizationService fileAuthService,
        CompanyProfileRepository companyProfiles,
        FileTokenService? fileTokenService = null,
        IStorageHealthService? storageHealth = null,
        IConfiguration? config = null)
    {
        _userService = userService ?? throw new ArgumentNullException(nameof(userService));
        _fileAuthService = fileAuthService ?? throw new ArgumentNullException(nameof(fileAuthService));
        _companyProfiles = companyProfiles ?? throw new ArgumentNullException(nameof(companyProfiles));
        _fileTokenService = fileTokenService;
        _storageHealth = storageHealth;
        _config = config;
    }

    public int Order => 1000;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var category = context.RouteData.Values["category"]?.ToString() ?? string.Empty;
        var contextId = context.RouteData.Values["contextId"]?.ToString() ?? string.Empty;
        var token = context.HttpContext.Request.Query["t"].FirstOrDefault();

        if (!FileHelper.IsValidPathSegment(category) || !FileHelper.IsValidPathSegment(contextId))
        {
            context.Result = new BadRequestObjectResult(SpokesResult.Failure("Invalid path parameters"));
            return;
        }

        if (_config?.GetValue<bool>("Spokes_DemoMode") == true)
        {
            var demoUser = _userService.GetEmployee(context.HttpContext.User);
            if (demoUser == null || !demoUser.IsAdmin)
            {
                context.Result = new ObjectResult(SpokesResult.Failure("Uploads are disabled in demo mode."))
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return;
            }
        }

        if (_storageHealth != null && !_storageHealth.IsUploadAllowed)
        {
            context.Result = new BadRequestObjectResult(SpokesResult.Failure(
                "Server storage is critically full (< 100 MB remaining). Uploads are temporarily paused to protect database integrity. Please free disk space on the server."));
            return;
        }

        var user = _userService.GetEmployee(context.HttpContext.User);
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned)
        {
            context.Result = new UnauthorizedObjectResult(SpokesResult.Failure("Authentication required.", "UNAUTHORIZED"));
            return;
        }

        var sessionId = context.HttpContext.User.FindFirst("SessionId")?.Value;
        var tokenValidation = _fileTokenService?.ValidateAccessToken(category, contextId, "*", token, user.Id, sessionId)
            ?? (string.IsNullOrEmpty(token) ? FileTokenValidationResult.None() : FileTokenValidationResult.Invalid());
        if (!tokenValidation.IsSuccess)
        {
            context.Result = new ObjectResult(SpokesResult.Failure("Upload Token Expired or Invalid"))
            {
                StatusCode = StatusCodes.Status410Gone
            };
            return;
        }

        if (!_fileAuthService.IsKnownCategory(category))
        {
            context.Result = new BadRequestObjectResult(SpokesResult.Failure($"Unknown file category: {category}"));
            return;
        }

        if (!_fileAuthService.CanUpload(category, contextId, user))
        {
            context.Result = new ObjectResult(SpokesResult.Failure("Access forbidden.", "FORBIDDEN"))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        long maxFileSize = Math.Max(1L, _companyProfiles.Get()?.MaxFileUploadSizeBytes ?? WebUiExtensions.DefaultMaxFileUploadSizeBytes);
        long maxBatchBodyBytes = maxFileSize > (long.MaxValue - MultipartFramingOverheadBytes) / FileController.MaxFilesPerUploadBatch
            ? long.MaxValue
            : (maxFileSize * FileController.MaxFilesPerUploadBatch) + MultipartFramingOverheadBytes;

        if (context.HttpContext.Request.ContentLength is long contentLength)
        {
            if (_storageHealth is { IsUploadAllowed: true, TotalBytes: > 0, AvailableFreeBytes: > 0 })
            {
                long availableBudget = _storageHealth.AvailableFreeBytes - StorageHealthService.CriticalThresholdBytes;
                if (contentLength > availableBudget)
                {
                    context.Result = new ObjectResult(SpokesResult.Failure("Insufficient server disk space to process this upload batch.", "INSUFFICIENT_STORAGE"))
                    {
                        StatusCode = StatusCodes.Status507InsufficientStorage
                    };
                    return;
                }
            }

            if (contentLength > maxBatchBodyBytes)
            {
                context.Result = new ObjectResult(SpokesResult.Failure($"Upload request exceeds maximum batch limit of {maxBatchBodyBytes} bytes.", "PAYLOAD_TOO_LARGE"))
                {
                    StatusCode = StatusCodes.Status413PayloadTooLarge
                };
                return;
            }
        }

        var bodySizeFeature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature != null && !bodySizeFeature.IsReadOnly)
        {
            bodySizeFeature.MaxRequestBodySize = maxBatchBodyBytes;
        }

        context.HttpContext.Features.Set<IFormFeature>(new FormFeature(
            context.HttpContext.Request,
            new FormOptions
            {
                MultipartBodyLengthLimit = maxBatchBodyBytes,
                ValueCountLimit = MaxMultipartValueCount
            }));

        await next();
    }
}
