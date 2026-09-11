namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Data;
using System;
using System.Collections.Generic;

public class Album : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (Album)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public List<string> SharedWithChannelIds { get; set; } = new();
    public List<string> ContributorUserIds { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsEncrypted { get; set; } = true;
    public Dictionary<string, string> EncryptedAlbumKeys { get; set; } = new();
    public List<AlbumMedia> Media { get; set; } = new();
}

public class AlbumMedia
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public string AddedByUserId { get; set; } = string.Empty;
    public bool HasServerThumbnail { get; set; } = false;
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string ThumbnailUrl
    {
        get
        {
            if (!HasServerThumbnail || string.IsNullOrWhiteSpace(FilePath)) 
                return FilePath;
                
            if (FilePath.StartsWith("/spokesapi/files/"))
            {
                return FilePath.Replace("/spokesapi/files/", "/spokesapi/files/thumb/");
            }
            
            return FilePath;
        }
    }
}
