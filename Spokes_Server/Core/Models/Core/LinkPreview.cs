namespace Spokes_Server.Core.Models.Core;

using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

/// <summary>
/// Cached link preview metadata extracted from Open Graph / HTML meta tags.
/// </summary>
public class LinkPreview
{
    public string Url { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? SiteName { get; set; }
    public string? FaviconUrl { get; set; }
    public bool IsYouTube { get; set; }
    public string? VideoEmbedUrl { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public bool Failed { get; set; }
}



