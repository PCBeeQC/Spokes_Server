using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Content;

/// <summary>
/// Singleton service that fetches and caches link preview metadata (Open Graph / meta tags).
/// </summary>
public class LinkPreviewService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, LinkPreview> _cache = new();
    private readonly ConcurrentDictionary<string, InFlightPreviewRequest> _inFlightFetches = new();
    private readonly ConcurrentDictionary<string, bool> _faviconValidityCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _inFlightFaviconChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _fetchLock = new(5); // max 5 concurrent fetches

    private sealed class InFlightPreviewRequest : IDisposable
    {
        private readonly object _lock = new();
        private int _waiters;
        private bool _disposed;
        private Task<LinkPreview?>? _task;

        public CancellationTokenSource Cts { get; } = new();

        public Task<LinkPreview?> GetOrStartTask(Func<InFlightPreviewRequest, Task<LinkPreview?>> factory)
        {
            lock (_lock)
            {
                return _task ??= factory(this);
            }
        }

        public bool TryAddWaiter()
        {
            lock (_lock)
            {
                if (_disposed || Cts.IsCancellationRequested)
                    return false;
                _waiters++;
                return true;
            }
        }

        public void RemoveWaiter(bool cancelled)
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                _waiters--;
                if (cancelled && _waiters <= 0 && !Cts.IsCancellationRequested)
                {
                    try { Cts.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                Cts.Dispose();
            }
        }
    }

    // Regex to extract URLs from message text
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s\)<>\]""'`]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex to detect YouTube URLs
    private static readonly Regex YouTubeRegex = new(
        @"(?:https?:\/\/)?(?:(?:[a-zA-Z0-9-]+\.)?youtube\.com\/(?:watch\?.*v=|embed\/|v\/|live\/|shorts\/)|youtu\.be\/)([^#\&\?]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [ActivatorUtilitiesConstructor]
    public LinkPreviewService(IHttpClientFactory httpClientFactory)
        : this(httpClientFactory?.CreateClient("LinkPreview"))
    {
    }

    internal LinkPreviewService(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
        }
        else
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; SpokesBot/1.0; +https://spokes.app)");
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
            _httpClient.MaxResponseContentBufferSize = 512 * 1024; // 512KB max
        }
    }

    internal ConcurrentDictionary<string, LinkPreview> Cache => _cache;

    /// <summary>
    /// Extract all URLs from message content.
    /// </summary>
    public List<string> ExtractUrls(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

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
    /// Gets a cached preview for the specified URL, or returns null if not cached, expired, or previously failed.
    /// Does not perform network requests; call <see cref="FetchPreviewAsync"/> to fetch and cache previews from the web.
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
    /// Fetch preview for a URL asynchronously. Returns the preview or null on failure or cancellation.
    /// </summary>
    public async Task<LinkPreview?> FetchPreviewAsync(string url, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return null;

        // Check cache first
        if (_cache.TryGetValue(url, out var cached))
        {
            if (DateTime.UtcNow - cached.FetchedAt < _cacheExpiry)
                return cached.Failed ? null : cached;
        }

        InFlightPreviewRequest inFlight;
        while (true)
        {
            inFlight = _inFlightFetches.GetOrAdd(url, static _ => new InFlightPreviewRequest());
            if (inFlight.TryAddWaiter())
                break;

            _inFlightFetches.TryRemove(KeyValuePair.Create(url, inFlight));
        }

        var fetchTask = inFlight.GetOrStartTask(req => ExecuteFetchAndCacheAsync(url, req));

        try
        {
            return await fetchTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            inFlight.RemoveWaiter(cancellationToken.IsCancellationRequested);
        }
    }

    private async Task<LinkPreview?> ExecuteFetchAndCacheAsync(string url, InFlightPreviewRequest request)
    {
        await Task.Yield();
        var ct = request.Cts.Token;
        var lockAcquired = false;
        try
        {
            await _fetchLock.WaitAsync(ct);
            lockAcquired = true;

            // Double-check after acquiring lock
            if (_cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.FetchedAt < _cacheExpiry)
                return cached.Failed ? null : cached;

            var preview = await FetchMetadataAsync(url, ct);
            _cache[url] = preview;
            return preview.Failed ? null : preview;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (lockAcquired)
                _fetchLock.Release();

            _inFlightFetches.TryRemove(KeyValuePair.Create(url, request));
            request.Dispose();
        }
    }

    private async Task<LinkPreview> FetchYouTubePreviewAsync(string url, string videoId, CancellationToken cancellationToken)
    {
        var preview = new LinkPreview
        {
            Url = url,
            IsYouTube = true,
            Title = "YouTube Video",
            SiteName = "YouTube",
            ImageUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg",
            VideoEmbedUrl = $"https://www.youtube.com/embed/{videoId}?autoplay=1",
            FaviconUrl = "https://www.youtube.com/favicon.ico"
        };

        try
        {
            var oEmbedUrl = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(url)}&format=json";
            using var request = new HttpRequestMessage(HttpMethod.Get, oEmbedUrl);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var jsonDoc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("title", out var titleProp) && !string.IsNullOrWhiteSpace(titleProp.GetString()))
                {
                    preview.Title = titleProp.GetString()!.Trim();
                }

                if (root.TryGetProperty("author_name", out var authorProp) && !string.IsNullOrWhiteSpace(authorProp.GetString()))
                {
                    preview.Description = authorProp.GetString()!.Trim();
                }

                if (root.TryGetProperty("thumbnail_url", out var thumbProp) && !string.IsNullOrWhiteSpace(thumbProp.GetString()))
                {
                    preview.ImageUrl = thumbProp.GetString()!.Trim();
                }

                if (root.TryGetProperty("provider_name", out var providerProp) && !string.IsNullOrWhiteSpace(providerProp.GetString()))
                {
                    preview.SiteName = providerProp.GetString()!.Trim();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Deterministic fallbacks are already initialized
        }

        return preview;
    }

    private async Task<LinkPreview> FetchMetadataAsync(string url, CancellationToken cancellationToken)
    {
        var ytMatch = YouTubeRegex.Match(url);
        if (ytMatch.Success && ytMatch.Groups.Count > 1)
        {
            var videoId = ytMatch.Groups[1].Value;
            if (videoId.Length == 11)
            {
                return await FetchYouTubePreviewAsync(url, videoId, cancellationToken);
            }
        }

        var preview = new LinkPreview { Url = url };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/html");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
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
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            int totalBytesRead = 0;
            while (totalBytesRead < buffer.Length)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalBytesRead, buffer.Length - totalBytesRead), cancellationToken);
                if (bytesRead == 0) break; // EOF
                totalBytesRead += bytesRead;
            }
            var html = System.Text.Encoding.UTF8.GetString(buffer, 0, totalBytesRead);

            var parser = new HtmlParser();
            using var doc = parser.ParseDocument(html);

            // Extract OG data
            var ogTitle = GetMetaContent(doc, "og:title") ?? GetMetaContent(doc, "twitter:title");
            if (!string.IsNullOrWhiteSpace(ogTitle))
                preview.Title = ogTitle;

            var ogDesc = GetMetaContent(doc, "og:description")
                       ?? GetMetaContent(doc, "twitter:description")
                       ?? GetMetaContent(doc, "description");
            if (!string.IsNullOrWhiteSpace(ogDesc))
                preview.Description = ogDesc;

            var ogImage = GetMetaContent(doc, "og:image") ?? GetMetaContent(doc, "twitter:image");
            if (!string.IsNullOrWhiteSpace(ogImage))
                preview.ImageUrl = ogImage;

            var ogSiteName = GetMetaContent(doc, "og:site_name");
            if (!string.IsNullOrWhiteSpace(ogSiteName))
                preview.SiteName = ogSiteName;

            // Fallback title from <title> tag
            if (string.IsNullOrEmpty(preview.Title))
            {
                var titleText = doc.Title;
                if (!string.IsNullOrWhiteSpace(titleText))
                    preview.Title = System.Net.WebUtility.HtmlDecode(titleText).Trim();
            }

            // Resolve relative image URLs
            if (!string.IsNullOrEmpty(preview.ImageUrl) && !preview.ImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(new Uri(url), preview.ImageUrl, out var absUri))
                    preview.ImageUrl = absUri.ToString();
            }

            // Extract favicon
            var faviconNode = doc.QuerySelector("link[rel~='icon'], link[rel='shortcut icon']");
            if (faviconNode != null)
            {
                var faviconHref = faviconNode.GetAttribute("href");
                if (!string.IsNullOrEmpty(faviconHref))
                {
                    if (!faviconHref.StartsWith("http", StringComparison.OrdinalIgnoreCase))
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
                if (!await ValidateFaviconAsync(preview.FaviconUrl, cancellationToken))
                {
                    preview.FaviconUrl = null;
                }
            }

            // If we got nothing useful, mark as failed
            if (string.IsNullOrEmpty(preview.Title) && string.IsNullOrEmpty(preview.Description))
                preview.Failed = true;

            return preview;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            preview.Failed = true;
            return preview;
        }
    }

    private async Task<bool> ValidateFaviconAsync(string faviconUrl, CancellationToken cancellationToken)
    {
        if (_faviconValidityCache.TryGetValue(faviconUrl, out var cachedValid))
            return cachedValid;

        var lazyCheck = _inFlightFaviconChecks.GetOrAdd(
            faviconUrl,
            url => new Lazy<Task<bool>>(() => CheckFaviconCoreAsync(url), LazyThreadSafetyMode.ExecutionAndPublication));

        return await lazyCheck.Value.WaitAsync(cancellationToken);
    }

    private async Task<bool> CheckFaviconCoreAsync(string url)
    {
        try
        {
            if (_faviconValidityCache.TryGetValue(url, out var cachedValid))
                return cachedValid;

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            var valid = headResponse.IsSuccessStatusCode;
            _faviconValidityCache[url] = valid;
            return valid;
        }
        catch
        {
            _faviconValidityCache[url] = false;
            return false;
        }
        finally
        {
            _inFlightFaviconChecks.TryRemove(url, out _);
        }
    }

    private static string? GetMetaContent(IHtmlDocument doc, string propertyOrName)
    {
        var node = doc.QuerySelector($"meta[property='{propertyOrName}'], meta[name='{propertyOrName}']");
        var content = node?.GetAttribute("content");
        return string.IsNullOrWhiteSpace(content) ? null : System.Net.WebUtility.HtmlDecode(content);
    }

    /// <summary>
    /// Get the domain name from a URL for display.
    /// </summary>
    public static string GetDomain(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
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
