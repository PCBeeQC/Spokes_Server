namespace Spokes_Server.Core.Models.Communication;

using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;

using System.Collections.Generic;
using System.Linq;

public class Album : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is Album other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public List<string> SharedWithChannelIds { get; set; } = [];
    public List<string> ContributorUserIds { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsEncrypted { get; set; } = true;
    public Dictionary<string, string> EncryptedAlbumKeys { get; set; } = [];
    public List<AlbumMedia> Media { get; set; } = [];

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (Media == null || string.IsNullOrWhiteSpace(Id))
        {
            return Enumerable.Empty<string>();
        }

        var expectedPrefix = $"/spokesapi/files/albums/{Id}/";
        return Media
            .Select(m => m.FilePath)
            .Where(p =>
            {
                if (string.IsNullOrWhiteSpace(p)) return false;
                var clean = p.Split('?', '#')[0].Trim();
                if (!clean.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                var fileSegment = clean[expectedPrefix.Length..];
                return !string.IsNullOrWhiteSpace(fileSegment) &&
                       !fileSegment.Contains('/', StringComparison.Ordinal) &&
                       !fileSegment.Contains('\\', StringComparison.Ordinal) &&
                       !fileSegment.Contains("..", StringComparison.Ordinal);
            });
    }
}

public class AlbumMedia : IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public string AddedByUserId { get; set; } = string.Empty;
    public bool HasServerThumbnail { get; set; }
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    public double? DurationSeconds { get; set; }

    [JsonIgnore]
    public bool CanUseServerThumbnail =>
        HasServerThumbnail || Spokes_Server.Core.Helpers.FileHelper.CanSelfHealThumbnail(FilePath);

    [JsonIgnore]
    public string ThumbnailUrl =>
        Spokes_Server.Core.Helpers.FileHelper.BuildThumbnailUrl(FilePath, CanUseServerThumbnail);

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(FilePath)) yield return FilePath;
    }
}
