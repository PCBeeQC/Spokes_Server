namespace Spokes_Server.Components.UI;

using System.Text.Json.Serialization;

public class LightboxMediaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public DateTime? AddedAt { get; set; }
    public long FileSizeBytes { get; set; }
    public bool HasServerThumbnail { get; set; }
    public bool IsEncrypted { get; set; }

    [JsonIgnore]
    public string ThumbnailUrl =>
        Spokes_Server.Core.Helpers.FileHelper.BuildThumbnailUrl(FilePath, HasServerThumbnail);
}
