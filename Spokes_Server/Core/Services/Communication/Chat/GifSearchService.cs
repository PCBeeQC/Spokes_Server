using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class GifSearchService : IGifSearchService
{
    public const string HttpClientName = "GifProvider";
    public const int DefaultPageSize = 20;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan PopulatedCacheTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EmptyCacheTtl = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpokesRelayClient _relayClient;
    private readonly CompanyProfileRepository _profileRepo;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly ILogger<GifSearchService> _logger;
    private readonly IMemoryCache? _cache;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly ConcurrentDictionary<string, Lazy<Task<GifSearchResult>>> _inFlight = new(StringComparer.Ordinal);

    public GifSearchService(
        IHttpClientFactory httpClientFactory,
        SpokesRelayClient relayClient,
        CompanyProfileRepository profileRepo,
        ServerConfigRepository serverConfigs,
        ILogger<GifSearchService> logger,
        IMemoryCache? cache = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? retryDelay = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _relayClient = relayClient ?? throw new ArgumentNullException(nameof(relayClient));
        _profileRepo = profileRepo ?? throw new ArgumentNullException(nameof(profileRepo));
        _serverConfigs = serverConfigs ?? throw new ArgumentNullException(nameof(serverConfigs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    public async Task<GifSearchResult> SearchGifsAsync(
        string query,
        int offset = 0,
        string? cursor = null,
        bool append = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
        {
            return new GifSearchResult { HasMore = false };
        }

        var profile = _profileRepo.Get();
        var provider = profile?.GifProvider ?? string.Empty;

        if (string.IsNullOrEmpty(provider) || provider == "None")
        {
            return new GifSearchResult
            {
                HasMore = false,
                ErrorMessage = "GIF provider not configured."
            };
        }

        var isDefaultProvider = provider == "Spokes Default Provider";
        var apiKey = profile?.GifApiKey ?? string.Empty;

        if (!isDefaultProvider && string.IsNullOrEmpty(apiKey))
        {
            return new GifSearchResult
            {
                HasMore = false,
                ErrorMessage = "GIF search is not available. No API key configured."
            };
        }

        var effectiveOffset = append ? Math.Max(0, offset) : 0;
        var effectiveCursor = append ? (cursor ?? string.Empty) : string.Empty;
        var cleanQuery = Regex.Replace(query.Trim(), @"\s+", " ");
        var normalizedQuery = cleanQuery.ToLowerInvariant();
        var klipyPage = ResolveKlipyPage(append, effectiveOffset, effectiveCursor);

        var cacheKey = BuildCacheKey(provider, normalizedQuery, klipyPage, effectiveOffset, effectiveCursor);
        if (_cache != null && _cache.TryGetValue(cacheKey, out GifSearchResult? cached) && cached != null)
        {
            return cached;
        }

        var lazyFlight = new Lazy<Task<GifSearchResult>>(
            () => ExecuteAndCacheAsync(
                cacheKey,
                provider,
                isDefaultProvider,
                apiKey,
                cleanQuery,
                klipyPage,
                effectiveOffset,
                effectiveCursor,
                append),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var activeFlight = _inFlight.GetOrAdd(cacheKey, lazyFlight);
        try
        {
            return await activeFlight.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(activeFlight, lazyFlight))
            {
                _ = RemoveWhenCompletedAsync(cacheKey, lazyFlight);
            }
        }
    }

    private async Task RemoveWhenCompletedAsync(string cacheKey, Lazy<Task<GifSearchResult>> entry)
    {
        try
        {
            await entry.Value.ConfigureAwait(false);
        }
        catch
        {
            // Swallow: exceptions are observed by callers awaiting entry.Value.
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<GifSearchResult>>>(cacheKey, entry));
        }
    }

    private async Task<GifSearchResult> ExecuteAndCacheAsync(
        string cacheKey,
        string provider,
        bool isDefaultProvider,
        string apiKey,
        string cleanQuery,
        int klipyPage,
        int effectiveOffset,
        string effectiveCursor,
        bool append)
    {
        GifSearchResult result;
        var escapedQuery = Uri.EscapeDataString(cleanQuery);

        try
        {
            if (isDefaultProvider)
            {
                result = await SearchViaRelayAsync(cleanQuery, klipyPage, effectiveOffset, append);
            }
            else if (provider == "Klipy")
            {
                var config = _serverConfigs.GetOrCreateGlobalConfig();
                var fingerprint = RelayTrialTicketService.ComputeFingerprint(config.ServerMasterRsaPublicKey) ?? string.Empty;
                var url = $"https://api.klipy.com/api/v1/{apiKey}/gifs/search?q={escapedQuery}&per_page={DefaultPageSize}&page={klipyPage}&format_filter=gif,webp";
                if (!string.IsNullOrEmpty(fingerprint))
                {
                    url += $"&customer_id={Uri.EscapeDataString(fingerprint)}";
                }

                result = await FetchDirectProviderAsync(provider, url, effectiveOffset, append, json => ParseKlipyJson(json, effectiveOffset, klipyPage));
            }
            else if (provider == "Tenor")
            {
                var url = $"https://tenor.googleapis.com/v2/search?q={escapedQuery}&key={apiKey}&limit={DefaultPageSize}";
                if (append && !string.IsNullOrEmpty(effectiveCursor))
                {
                    url += $"&pos={Uri.EscapeDataString(effectiveCursor)}";
                }

                result = await FetchDirectProviderAsync(provider, url, effectiveOffset, append, json => ParseTenorJson(json, effectiveOffset));
            }
            else if (provider == "Giphy")
            {
                var url = $"https://api.giphy.com/v1/gifs/search?api_key={apiKey}&q={escapedQuery}&limit={DefaultPageSize}&offset={effectiveOffset}";
                result = await FetchDirectProviderAsync(provider, url, effectiveOffset, append, json => ParseGiphyJson(json, effectiveOffset));
            }
            else
            {
                result = new GifSearchResult
                {
                    HasMore = false,
                    ErrorMessage = $"Unknown GIF provider: {provider}"
                };
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error while searching GIFs with provider {Provider}", provider);
            result = new GifSearchResult
            {
                HasMore = false,
                ErrorMessage = isDefaultProvider
                    ? "Spokes GIF relay is currently unreachable. Please try again later."
                    : $"GIF Provider HTTP Error: {ex.Message}"
            };
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "GIF search timed out with provider {Provider}", provider);
            result = new GifSearchResult
            {
                HasMore = false,
                ErrorMessage = "GIF search timed out. Please try again."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching GIFs with provider {Provider}", provider);
            result = new GifSearchResult
            {
                HasMore = false,
                ErrorMessage = "Error fetching GIFs. Please try again."
            };
        }

        if (_cache != null && string.IsNullOrEmpty(result.ErrorMessage))
        {
            var ttl = result.Results.Count > 0 ? PopulatedCacheTtl : EmptyCacheTtl;
            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            });
        }

        return result;
    }

    private async Task<GifSearchResult> FetchDirectProviderAsync(
        string provider,
        string url,
        int effectiveOffset,
        bool append,
        Func<string, GifSearchResult> parser)
    {
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = new CancellationTokenSource(_requestTimeout);
            try
            {
                using var response = await httpClient.GetAsync(url, attemptCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(attemptCts.Token);
                    return parser(json);
                }

                if (IsTransientStatus(response.StatusCode) && attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "GIF provider {Provider} returned transient HTTP {StatusCode} (attempt {Attempt}/{MaxAttempts}); retrying.",
                        provider,
                        (int)response.StatusCode,
                        attempt,
                        maxAttempts);
                    await DelayRetryAsync();
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return new GifSearchResult
                    {
                        HasMore = false,
                        ErrorMessage = "GIF search rate limit reached. Please wait a moment."
                    };
                }

                if (append && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                {
                    _logger.LogInformation(
                        "GIF provider {Provider} returned HTTP {StatusCode} during pagination; treating as end of results.",
                        provider,
                        (int)response.StatusCode);
                    return new GifSearchResult
                    {
                        HasMore = false,
                        NewOffset = effectiveOffset
                    };
                }

                _logger.LogError("GIF provider {Provider} rejected request with HTTP {StatusCode}", provider, response.StatusCode);
                return new GifSearchResult
                {
                    HasMore = false,
                    ErrorMessage = $"GIF Provider HTTP Error: {(int)response.StatusCode}"
                };
            }
            catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Transient failure contacting GIF provider {Provider} (attempt {Attempt}/{MaxAttempts}); retrying.",
                    provider,
                    attempt,
                    maxAttempts);
                await DelayRetryAsync();
            }
        }

        return new GifSearchResult
        {
            HasMore = false,
            ErrorMessage = "Error fetching GIFs. Please try again."
        };
    }

    private async Task<GifSearchResult> SearchViaRelayAsync(
        string query,
        int page,
        int offset,
        bool append)
    {
        var config = _serverConfigs.GetOrCreateGlobalConfig();
        var fingerprint = RelayTrialTicketService.ComputeFingerprint(config.ServerMasterRsaPublicKey) ?? string.Empty;

        var profile = _profileRepo.Get();
        var relayPayload = new GifRelayPayload
        {
            Query = query,
            Page = page,
            PerPage = DefaultPageSize,
            ServerFingerprint = fingerprint,
            ServerId = config.DatabaseCreationId,
            LicensePayload = profile?.LicensePayload
        };

        var relayUrl = _relayClient.ResolveGifRelayUrl();

        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await _relayClient.PostJsonAsync(relayUrl, relayPayload, CancellationToken.None, _requestTimeout);
                if (response.IsSuccessStatusCode)
                {
                    return ParseKlipyJson(response.RawBody, offset, page);
                }

                if (IsTransientStatus(response.StatusCode) && attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "GIF relay returned transient HTTP {StatusCode} (code: {Code}) on attempt {Attempt}/{MaxAttempts}; retrying.",
                        (int)response.StatusCode,
                        response.Code ?? "none",
                        attempt,
                        maxAttempts);
                    await DelayRetryAsync();
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return new GifSearchResult
                    {
                        HasMore = false,
                        ErrorMessage = "GIF search rate limit reached. Please wait a moment."
                    };
                }

                if (append && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                {
                    _logger.LogInformation(
                        "GIF relay returned HTTP {StatusCode} (code: {Code}) on page {Page} during pagination; treating as end of results.",
                        (int)response.StatusCode,
                        response.Code ?? "none",
                        page);
                    return new GifSearchResult
                    {
                        HasMore = false,
                        NewOffset = offset
                    };
                }

                if (response.StatusCode is HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout)
                {
                    _logger.LogWarning(
                        "GIF relay timed out with HTTP {StatusCode} (code: {Code}) on page {Page}.",
                        (int)response.StatusCode,
                        response.Code ?? "none",
                        page);
                    return new GifSearchResult
                    {
                        HasMore = false,
                        ErrorMessage = "GIF search timed out. Please try again."
                    };
                }

                _logger.LogError(
                    "GIF relay rejected request with status code {StatusCode} (code: {Code}, detail: {Detail})",
                    response.StatusCode,
                    response.Code ?? "none",
                    response.Detail ?? "none");
                return new GifSearchResult
                {
                    HasMore = false,
                    ErrorMessage = "Spokes GIF relay is currently unavailable."
                };
            }
            catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Transient failure contacting Spokes GIF relay on page {Page} (attempt {Attempt}/{MaxAttempts}); retrying.",
                    page,
                    attempt,
                    maxAttempts);
                await DelayRetryAsync();
            }
        }

        return new GifSearchResult
        {
            HasMore = false,
            ErrorMessage = "Spokes GIF relay is currently unavailable."
        };
    }

    private Task DelayRetryAsync() =>
        _retryDelay > TimeSpan.Zero ? Task.Delay(_retryDelay) : Task.CompletedTask;

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static int ResolveKlipyPage(bool append, int effectiveOffset, string effectiveCursor)
    {
        if (!append)
        {
            return 1;
        }

        if (int.TryParse(effectiveCursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPage) && parsedPage > 0)
        {
            return parsedPage;
        }

        return (effectiveOffset / DefaultPageSize) + 1;
    }

    private static string BuildCacheKey(string provider, string normalizedQuery, int klipyPage, int effectiveOffset, string effectiveCursor)
    {
        if (provider is "Spokes Default Provider" or "Klipy")
        {
            return $"gif_search:{provider}:{normalizedQuery}:p{klipyPage}";
        }

        if (provider == "Tenor")
        {
            return $"gif_search:{provider}:{normalizedQuery}:c{effectiveCursor}";
        }

        return $"gif_search:{provider}:{normalizedQuery}:o{effectiveOffset}";
    }

    public static GifSearchResult ParseKlipyJson(string json, int currentOffset, int currentPage = 1)
    {
        var root = JsonNode.Parse(json);
        var dataObj = root?["data"] as JsonObject;
        var results = root as JsonArray
                   ?? root?["data"] as JsonArray
                   ?? dataObj?["data"] as JsonArray
                   ?? root?["results"] as JsonArray;

        var returnedCount = results?.Count ?? 0;
        var resolvedPage = TryReadInt(dataObj?["current_page"]) ?? TryReadInt(root?["current_page"]) ?? Math.Max(1, currentPage);
        var explicitHasNext = TryReadBool(dataObj?["has_next"]) ?? TryReadBool(root?["has_next"]);
        var newOffset = currentOffset + returnedCount;

        var items = new List<GifItem>();
        if (results != null)
        {
            foreach (var res in results)
            {
                if (string.Equals(res?["type"]?.ToString(), "ad", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var files = res?["file"] ?? res?["files"];

                // Full chat URL: prefer highest quality animated WebP (24-bit color + ~70% smaller), then GIF fallbacks.
                var fullObj = PickFirstWithUrl(
                    files?["hd"]?["webp"],
                    files?["md"]?["webp"],
                    files?["hd"]?["gif"],
                    files?["md"]?["gif"],
                    files?["sm"]?["webp"],
                    files?["sm"]?["gif"],
                    files?["hd"],
                    res);
                var gifUrl = fullObj?["url"]?.ToString();

                // Picker preview URL: prefer lightweight animated WebP thumbnails so 20-tile grids load rapidly.
                var previewObj = PickFirstWithUrl(
                    files?["sm"]?["webp"],
                    files?["xs"]?["webp"],
                    files?["sm"]?["gif"],
                    files?["xs"]?["gif"],
                    files?["md"]?["webp"],
                    files?["md"]?["gif"],
                    files?["preview"]?["gif"],
                    files?["preview"],
                    fullObj);
                var previewUrl = previewObj?["url"]?.ToString() ?? gifUrl;

                var dimsObj = HasDimensions(previewObj)
                    ? previewObj
                    : HasDimensions(fullObj)
                        ? fullObj
                        : res;

                TryAddGifItem(items, gifUrl, previewUrl, dimsObj?["width"], dimsObj?["height"]);
            }
        }

        var hasMore = explicitHasNext.HasValue
            ? explicitHasNext.Value && items.Count > 0
            : returnedCount >= DefaultPageSize && items.Count > 0;

        return new GifSearchResult
        {
            Results = items,
            HasMore = hasMore,
            NextCursor = hasMore ? (resolvedPage + 1).ToString(CultureInfo.InvariantCulture) : string.Empty,
            NewOffset = newOffset
        };
    }

    public static GifSearchResult ParseTenorJson(string json, int currentOffset)
    {
        var root = JsonNode.Parse(json);
        var nextCursor = root?["next"]?.ToString() ?? string.Empty;
        var hasMore = !string.IsNullOrEmpty(nextCursor);

        var items = new List<GifItem>();
        if (root?["results"] is JsonArray results)
        {
            foreach (var res in results)
            {
                var media = res?["media_formats"];
                var gifUrl = media?["webp"]?["url"]?.ToString() ?? media?["gif"]?["url"]?.ToString();
                var tinyUrl = media?["tinywebp"]?["url"]?.ToString()
                           ?? media?["tinygif"]?["url"]?.ToString()
                           ?? gifUrl;
                var dims = media?["tinywebp"]?["dims"] as JsonArray
                        ?? media?["tinygif"]?["dims"] as JsonArray
                        ?? media?["webp"]?["dims"] as JsonArray
                        ?? media?["gif"]?["dims"] as JsonArray;
                TryAddGifItem(items, gifUrl, tinyUrl, dims is { Count: >= 2 } ? dims[0] : null, dims is { Count: >= 2 } ? dims[1] : null);
            }
        }

        return new GifSearchResult
        {
            Results = items,
            HasMore = hasMore && items.Count > 0,
            NextCursor = nextCursor,
            NewOffset = currentOffset + items.Count
        };
    }

    public static GifSearchResult ParseGiphyJson(string json, int currentOffset)
    {
        var root = JsonNode.Parse(json);
        var pagination = root?["pagination"];
        var count = pagination?["count"]?.GetValue<int>() ?? 0;
        var hasMore = count >= DefaultPageSize;

        var items = new List<GifItem>();
        if (root?["data"] is JsonArray results)
        {
            foreach (var res in results)
            {
                var images = res?["images"];
                var gifUrl = images?["original"]?["webp"]?.ToString() ?? images?["original"]?["url"]?.ToString();
                var previewUrl = images?["fixed_height_small"]?["webp"]?.ToString()
                              ?? images?["fixed_height_small"]?["url"]?.ToString()
                              ?? gifUrl;
                var targetImg = images?["fixed_height_small"] ?? images?["original"];
                TryAddGifItem(items, gifUrl, previewUrl, targetImg?["width"], targetImg?["height"]);
            }
        }

        return new GifSearchResult
        {
            Results = items,
            HasMore = hasMore && items.Count > 0,
            NewOffset = currentOffset + count
        };
    }

    private static JsonNode? PickFirstWithUrl(params JsonNode?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var url = candidate?["url"]?.ToString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasDimensions(JsonNode? node) =>
        node?["width"] != null && node?["height"] != null;

    private static bool? TryReadBool(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<bool>(out var b)) return b;
            if (val.TryGetValue<int>(out var i)) return i != 0;
            if (val.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    private static int? TryReadInt(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<int>(out var i)) return i;
            if (val.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return null;
    }

    private static void TryAddGifItem(List<GifItem> items, string? gifUrl, string? previewUrl, JsonNode? widthNode, JsonNode? heightNode)
    {
        if (string.IsNullOrEmpty(gifUrl) || items.Any(g => g.Url == gifUrl))
            return;

        double? aspectRatio = null;
        if (double.TryParse(widthNode?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var w) &&
            double.TryParse(heightNode?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var h) && h > 0)
        {
            aspectRatio = w / h;
        }

        items.Add(new GifItem
        {
            Url = gifUrl,
            PreviewUrl = previewUrl ?? gifUrl,
            AspectRatio = aspectRatio
        });
    }
}
