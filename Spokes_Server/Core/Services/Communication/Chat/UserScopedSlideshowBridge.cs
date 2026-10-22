namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

/// <summary>
/// Bridge implementation running within the authenticated user's session scope.
/// Fulfills media requests for the slideshow API exclusively through FileController,
/// ensuring both authorization checks and decryption are performed under the presenter's credentials.
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

    public IActionResult GetMedia(string mediaId, bool thumb)
        => GetMedia(mediaId, thumb, null);

    public IActionResult GetMedia(string mediaId, bool thumb, ActionContext? actionContext)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || !_allowedMedia.TryGetValue(mediaId, out var fileName))
        {
            return new NotFoundObjectResult("Media item not found in slideshow album.");
        }

        var fileController = _serviceProvider.GetService<FileController>() 
            ?? ActivatorUtilities.CreateInstance<FileController>(_serviceProvider);

        if (actionContext != null)
        {
            fileController.ControllerContext = new ControllerContext(actionContext);
        }

        return fileController.ServeFileForUser(
            category: "albums",
            contextId: _albumId,
            fileName: fileName,
            user: _user,
            thumb: thumb,
            tokenEncryptionKey: _plainAlbumKey);
    }
}
