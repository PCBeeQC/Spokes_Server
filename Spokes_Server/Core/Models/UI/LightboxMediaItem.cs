namespace Spokes_Server.Core.Models.UI;

public class LightboxMediaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public DateTime? AddedAt { get; set; }
    public long FileSizeBytes { get; set; }
    public bool HasServerThumbnail { get; set; }

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
            else if (FilePath.StartsWith("/internal/attachments/"))
            {
                return FilePath.Replace("/internal/attachments/", "/internal/attachments/thumb/");
            }
            
            return FilePath;
        }
    }
}
