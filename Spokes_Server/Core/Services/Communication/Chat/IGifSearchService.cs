using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Chat;

public interface IGifSearchService
{
    Task<GifSearchResult> SearchGifsAsync(
        string query,
        int offset = 0,
        string? cursor = null,
        bool append = false,
        CancellationToken cancellationToken = default);
}

public class GifSearchResult
{
    public List<GifItem> Results { get; set; } = new();
    public bool HasMore { get; set; } = true;
    public string NextCursor { get; set; } = string.Empty;
    public int NewOffset { get; set; }
    public string? ErrorMessage { get; set; }
}

public class GifItem
{
    public string Url { get; set; } = string.Empty;
    public string PreviewUrl { get; set; } = string.Empty;
    public double? AspectRatio { get; set; }
}
