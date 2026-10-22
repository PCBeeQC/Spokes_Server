namespace Spokes_Server.Core.Models.Communication;

using System;
using System.Collections.Generic;

/// <summary>
/// Represents an active, ephemeral TV slideshow presentation session.
/// </summary>
public class SlideshowSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 6-digit numeric pairing PIN (e.g. "849215") shown on the TV screen.
    /// </summary>
    public string PairingPin { get; set; } = string.Empty;

    /// <summary>
    /// SignalR Connection ID of the TV / display stage client.
    /// </summary>
    public string TvConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// User ID of the authenticated host who paired and controls the session.
    /// </summary>
    public string HostUserId { get; set; } = string.Empty;

    /// <summary>
    /// SignalR Connection ID of the host phone remote.
    /// </summary>
    public string HostConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// ID of the album being presented.
    /// </summary>
    public string AlbumId { get; set; } = string.Empty;

    /// <summary>
    /// Current slide index on the TV stage.
    /// </summary>
    public int CurrentIndex { get; set; } = 0;

    /// <summary>
    /// Whether the session has been successfully paired with an authorized host.
    /// </summary>
    public bool IsPaired { get; set; } = false;

    /// <summary>
    /// UTC timestamp when the pairing code was generated.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Expiration time for the pairing code (default 5 minutes).
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(5);

    /// <summary>
    /// Last activity timestamp to enforce idle timeouts.
    /// </summary>
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of failed pairing attempts against this session.
    /// </summary>
    public int FailedAttempts { get; set; } = 0;

    /// <summary>
    /// Cached stage payload containing media metadata for the active presentation.
    /// </summary>
    public SlideshowStagePayload? StagePayload { get; set; }

    /// <summary>
    /// User-scoped file bridge populated by the authenticated presenter during active sessions.
    /// Acts as the sole pipeline through FileController for the Slideshow API.
    /// Immediately cleared when the session ends or expires.
    /// </summary>
    public ISlideshowFileBridge? FileBridge { get; set; }
}

/// <summary>
/// User-scoped bridge interface to fetch media items through FileController.
/// </summary>
public interface ISlideshowFileBridge
{
    Microsoft.AspNetCore.Mvc.IActionResult GetMedia(string mediaId, bool thumb);
}

/// <summary>
/// Lightweight payload transferred to the TV stage upon session pairing.
/// </summary>
public class SlideshowStagePayload
{
    public string SessionId { get; set; } = string.Empty;
    public string AlbumId { get; set; } = string.Empty;
    public string AlbumTitle { get; set; } = string.Empty;
    public int InitialIndex { get; set; }
    public List<SlideshowMediaItem> Items { get; set; } = [];
}

/// <summary>
/// Pre-tokenized media item ready for rendering on the TV stage.
/// </summary>
public class SlideshowMediaItem
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    private string? _thumbnailUrl;
    public string ThumbnailUrl
    {
        get => !string.IsNullOrEmpty(_thumbnailUrl) ? _thumbnailUrl : Url;
        set => _thumbnailUrl = value;
    }

    public string ContentType { get; set; } = string.Empty;
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    public double? DurationSeconds { get; set; }
    public bool IsPortrait => ImageHeight.HasValue && ImageWidth.HasValue && ImageHeight.Value > ImageWidth.Value;
    public bool IsPortraitPhoto => IsPortrait && !ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    public DateTime? AddedAt { get; set; }
    public string AddedByUserName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
}
