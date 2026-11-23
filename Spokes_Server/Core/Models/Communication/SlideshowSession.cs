namespace Spokes_Server.Core.Models.Communication;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Distinguishes the display surface that originated a slideshow session.
/// </summary>
public enum SlideshowDisplayKind
{
    BlazorPinStage = 0,
    SignalRCastReceiver = 1,
    DirectExternalDisplay = 2
}

/// <summary>
/// Represents an active, ephemeral TV slideshow presentation session.
/// </summary>
public class SlideshowSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 256-bit Base64Url capability secret proving possession of the TV/display stage.
    /// </summary>
    public string TvSecret { get; set; } = string.Empty;

    /// <summary>
    /// Display surface kind that created this session.
    /// </summary>
    public SlideshowDisplayKind DisplayKind { get; set; } = SlideshowDisplayKind.BlazorPinStage;

    /// <summary>
    /// Single-circuit binding token for DirectExternalDisplay stages (/slideshow/stage/{id}).
    /// </summary>
    [JsonIgnore]
    public string? BoundDirectStageToken { get; set; }

    /// <summary>
    /// Remote IP address that created an unpaired session, used for per-IP unpaired session caps.
    /// </summary>
    [JsonIgnore]
    public string? CreatorIpAddress { get; set; }

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
    System.Threading.Tasks.Task<Microsoft.AspNetCore.Mvc.IActionResult> GetMediaAsync(
        string mediaId,
        bool thumb,
        System.Threading.CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Builds a tokenized stage payload for broadcasting or opening a slideshow album session.
    /// </summary>
    public static SlideshowStagePayload Create(
        string sessionId,
        string tvSecret,
        Album album,
        int initialIndex = 0,
        Func<string, string, string>? tokenizeUrl = null)
    {
        var items = new List<SlideshowMediaItem>(album.Media.Count);
        foreach (var media in album.Media)
        {
            string baseUrl = $"/spokesapi/slideshow/{sessionId}/media/{media.Id}";
            string url = tokenizeUrl != null ? tokenizeUrl(baseUrl, tvSecret) : baseUrl;
            bool isVideoWithoutThumb = media.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && !media.HasServerThumbnail;
            string thumbUrl = isVideoWithoutThumb
                ? string.Empty
                : (tokenizeUrl != null ? tokenizeUrl($"{baseUrl}/thumb", tvSecret) : $"{baseUrl}/thumb");

            items.Add(new SlideshowMediaItem
            {
                Id = media.Id,
                FileName = media.FileName,
                Url = url,
                ThumbnailUrl = thumbUrl,
                ContentType = media.ContentType,
                ImageWidth = media.ImageWidth,
                ImageHeight = media.ImageHeight,
                DurationSeconds = media.DurationSeconds,
                AddedAt = media.AddedAt,
                AddedByUserName = string.Empty,
                FileSizeBytes = media.FileSizeBytes
            });
        }

        return new SlideshowStagePayload
        {
            SessionId = sessionId,
            AlbumId = album.Id,
            AlbumTitle = album.Title,
            InitialIndex = Math.Clamp(initialIndex, 0, Math.Max(0, items.Count - 1)),
            Items = items
        };
    }
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
