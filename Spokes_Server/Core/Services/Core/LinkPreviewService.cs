using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.RegularExpressions;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using HtmlAgilityPack;

namespace Spokes_Server.Core.Services.Core;

/// <summary>
/// Singleton service that fetches and caches link preview metadata (Open Graph / meta tags).
/// </summary>
public class LinkPreviewService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, LinkPreview> _cache = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _fetchLock = new(5); // max 5 concurrent fetches

    // Regex to extract URLs from message text
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s\)<>\]""'`]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex to detect YouTube URLs
    private static readonly Regex YouTubeRegex = new(
        @"(?:https?:\/\/)?(?:www\.)?(?:youtube\.com\/(?:watch\?.*v=|embed\/|v\/|live\/|shorts\/)|youtu\.be\/)([^#\&\?]*).*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public LinkPreviewService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; SpokesBot/1.0; +https://spokes.app)");
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
        _httpClient.MaxResponseContentBufferSize = 512 * 1024; // 512KB max
    }

    /// <summary>
    /// Extract all URLs from message content.
    /// </summary>
    public List<string> ExtractUrls(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new List<string>();

        // Strip code blocks to avoid extracting URLs from code snippets
        var strippedContent = Regex.Replace(content, @"\[code(?:=([^\]]+))?\].*?\[/code\]", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        strippedContent = Regex.Replace(strippedContent, @"```.*?```", "", RegexOptions.Singleline);

        return UrlRegex.Matches(strippedContent)
            .Select(m => m.Value.TrimEnd('.', ',', '!', '?', ')', ';', ':'))
            .Distinct()
            .Take(3) // max 3 previews per message
            .ToList();
    }

    /// <summary>
    /// Get cached preview or fetch from URL. Returns null while fetching (fire-and-forget pattern).
    /// </summary>
    public LinkPreview? GetPreview(string url)
    {
        if (_cache.TryGetValue(url, out var cached))
        {
            if (DateTime.UtcNow - cached.FetchedAt < _cacheExpiry)
                return cached.Failed ? null : cached;

            // Expired — remove and refetch
            _cache.TryRemove(url, out _);
        }

        return null;
    }

    /// <summary>
    /// Fetch preview for a URL asynchronously. Returns the preview or null on failure.
    /// </summary>
    public async Task<LinkPreview?> FetchPreviewAsync(string url)
    {
        // Check cache first
        if (_cache.TryGetValue(url, out var cached))
        {
            if (DateTime.UtcNow - cached.FetchedAt < _cacheExpiry)
                return cached.Failed ? null : cached;
        }

        await _fetchLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cache.TryGetValue(url, out cached) && DateTime.UtcNow - cached.FetchedAt < _cacheExpiry)
                return cached.Failed ? null : cached;

            var preview = await FetchMetadataAsync(url);
            _cache[url] = preview;
            return preview.Failed ? null : preview;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private async Task<LinkPreview> FetchMetadataAsync(string url)
    {
        var preview = new LinkPreview { Url = url };

        var ytMatch = YouTubeRegex.Match(url);
        if (ytMatch.Success && ytMatch.Groups.Count > 1)
        {
            var videoId = ytMatch.Groups[1].Value;
            if (videoId.Length == 11)
            {
                preview.IsYouTube = true;
                preview.VideoEmbedUrl = $"https://www.youtube.com/embed/{videoId}?autoplay=1";
                // Fallback image in case OG fetch fails or doesn't have one
                preview.ImageUrl = $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg";
            }
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/html");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                if (preview.IsYouTube)
                {
                    preview.Title = "YouTube Video";
                    preview.SiteName = "YouTube";
                    return preview;
                }
                preview.Failed = true;
                return preview;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                preview.Failed = true;
                return preview;
            }

            // Read up to 256KB for metadata extraction to ensure we capture the whole <head>
            var buffer = new byte[256 * 1024];
            using var stream = await response.Content.ReadAsStreamAsync();
            int totalBytesRead = 0;
            while (totalBytesRead < buffer.Length)
            {
                var bytesRead = await stream.ReadAsync(buffer, totalBytesRead, buffer.Length - totalBytesRead);
                if (bytesRead == 0) break; // EOF
                totalBytesRead += bytesRead;
            }
            var html = System.Text.Encoding.UTF8.GetString(buffer, 0, totalBytesRead);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Extract OG data
            preview.Title = GetMetaContent(doc, "og:title") ?? GetMetaContent(doc, "twitter:title");
            
            preview.Description = GetMetaContent(doc, "og:description")
                               ?? GetMetaContent(doc, "twitter:description")
                               ?? GetMetaContent(doc, "description");
                               
            preview.ImageUrl = GetMetaContent(doc, "og:image") ?? GetMetaContent(doc, "twitter:image");
            preview.SiteName = GetMetaContent(doc, "og:site_name");

            // Fallback title from <title> tag
            if (string.IsNullOrEmpty(preview.Title))
            {
                var titleNode = doc.DocumentNode.SelectSingleNode("//title");
                if (titleNode != null)
                    preview.Title = System.Net.WebUtility.HtmlDecode(titleNode.InnerText).Trim();
            }

            // Resolve relative image URLs
            if (!string.IsNullOrEmpty(preview.ImageUrl) && !preview.ImageUrl.StartsWith("http"))
            {
                if (Uri.TryCreate(new Uri(url), preview.ImageUrl, out var absUri))
                    preview.ImageUrl = absUri.ToString();
            }

            // Extract favicon
            var faviconNode = doc.DocumentNode.SelectSingleNode("//link[contains(@rel, 'icon') or contains(@rel, 'shortcut icon')]");
            if (faviconNode != null)
            {
                var faviconHref = faviconNode.GetAttributeValue("href", string.Empty);
                if (!string.IsNullOrEmpty(faviconHref))
                {
                    if (!faviconHref.StartsWith("http"))
                    {
                        if (Uri.TryCreate(new Uri(url), faviconHref, out var absFavicon))
                            faviconHref = absFavicon.ToString();
                    }
                    preview.FaviconUrl = faviconHref;
                }
            }

            // Fallback favicon
            if (string.IsNullOrEmpty(preview.FaviconUrl))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
                    preview.FaviconUrl = $"{baseUri.Scheme}://{baseUri.Host}/favicon.ico";
            }

            // Validate the favicon URL actually exists (HEAD request) to avoid
            // flooding the client with 404 errors for broken favicon images.
            if (!string.IsNullOrEmpty(preview.FaviconUrl))
            {
                try
                {
                    using var headRequest = new HttpRequestMessage(HttpMethod.Head, preview.FaviconUrl);
                    using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead);
                    if (!headResponse.IsSuccessStatusCode)
                        preview.FaviconUrl = null;
                }
                catch
                {
                    preview.FaviconUrl = null;
                }
            }

            // If we got nothing useful and it's not a youtube video with fallbacks, mark as failed
            if (string.IsNullOrEmpty(preview.Title) && string.IsNullOrEmpty(preview.Description) && !preview.IsYouTube)
                preview.Failed = true;

            return preview;
        }
        catch
        {
            if (preview.IsYouTube)
            {
                preview.Title ??= "YouTube Video";
                preview.SiteName ??= "YouTube";
                return preview;
            }
            preview.Failed = true;
            return preview;
        }
    }

    private static string? GetMetaContent(HtmlDocument doc, string propertyOrName)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//meta[@property='{propertyOrName}' or @name='{propertyOrName}']");
        var content = node?.GetAttributeValue("content", null);
        return string.IsNullOrWhiteSpace(content) ? null : System.Net.WebUtility.HtmlDecode(content);
    }

    /// <summary>
    /// Get the domain name from a URL for display.
    /// </summary>
    public static string GetDomain(string url)
    {
        try
        {
            return new Uri(url).Host.Replace("www.", "");
        }
        catch
        {
            return url;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _fetchLock.Dispose();
    }
}


