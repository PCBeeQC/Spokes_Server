namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Storage;

/// <summary>
/// Bridge implementation running within the authenticated user's session scope.
/// Fulfills media requests for the slideshow API exclusively through FileController,
/// ensuring both live authorization checks and decryption are performed under the presenter's credentials.
/// </summary>
public class UserScopedSlideshowBridge : ISlideshowFileBridge
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Employee _user;
    private readonly string _albumId;
    private readonly string? _plainAlbumKey;
    private readonly Dictionary<string, string> _allowedMedia; // mediaId -> fileName

    public UserScopedSlideshowBridge(
        IServiceProvider serviceProvider,
        Employee user,
        string albumId,
        string? plainAlbumKey,
        Dictionary<string, string> allowedMedia)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _albumId = albumId ?? throw new ArgumentNullException(nameof(albumId));
        _plainAlbumKey = plainAlbumKey;
        _allowedMedia = allowedMedia ?? throw new ArgumentNullException(nameof(allowedMedia));
    }

    public Task<IActionResult> GetMediaAsync(string mediaId, bool thumb, CancellationToken cancellationToken = default)
        => GetMediaAsync(mediaId, thumb, null, cancellationToken);

    public async Task<IActionResult> GetMediaAsync(string mediaId, bool thumb, ActionContext? actionContext, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || !_allowedMedia.TryGetValue(mediaId, out var fileName))
        {
            return new NotFoundObjectResult("Media item not found in slideshow album.");
        }

        var employeeRepo = _serviceProvider.GetService<EmployeeRepository>()
            ?? _serviceProvider.GetService<Database>()?.Employees;
        var liveUser = employeeRepo != null ? employeeRepo.GetById(_user.Id) : _user;

        if (liveUser == null ||
            !liveUser.IsActive ||
            liveUser.IsBanned ||
            !FileAccessPermissionHelper.HasAssignedPermission(liveUser, AppPermissions.Albums.View))
        {
            return new ForbidResult();
        }

        var fileController = _serviceProvider.GetService<FileController>()
            ?? ActivatorUtilities.CreateInstance<FileController>(_serviceProvider);

        if (actionContext != null)
        {
            fileController.ControllerContext = new ControllerContext(actionContext);
        }

        var albumRepo = _serviceProvider.GetService<AlbumRepository>()
            ?? _serviceProvider.GetService<Database>()?.Albums;
        var albumService = _serviceProvider.GetService<AlbumService>();
        if (albumRepo != null)
        {
            var album = albumRepo.GetById(_albumId);
            if (album == null)
            {
                var demoResult = fileController.GetDemoMedia(fileName);
                if (demoResult is not NotFoundResult && demoResult is not NotFoundObjectResult && demoResult is not ForbidResult)
                {
                    return demoResult;
                }
                return new ForbidResult();
            }

            if (albumService != null && !albumService.CanUserViewAlbum(album, liveUser.Id))
            {
                return new ForbidResult();
            }
        }

        var result = await fileController.ServeFileForUserAsync(
            category: "albums",
            contextId: _albumId,
            fileName: fileName,
            user: liveUser,
            thumb: thumb,
            tokenEncryptionKey: _plainAlbumKey,
            cancellationToken: cancellationToken);

        if (result is NotFoundResult || result is NotFoundObjectResult)
        {
            var demoResult = fileController.GetDemoMedia(fileName);
            if (demoResult is not NotFoundResult && demoResult is not NotFoundObjectResult && demoResult is not ForbidResult)
            {
                return demoResult;
            }
        }

        return result;
    }
}
