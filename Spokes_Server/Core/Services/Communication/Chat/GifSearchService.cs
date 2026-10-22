using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class GifSearchService : IGifSearchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpokesOptions _options;
    private readonly CompanyProfileRepository _profileRepo;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly ILogger<GifSearchService> _logger;

    public GifSearchService(
        IHttpClientFactory httpClientFactory,
        IOptions<SpokesOptions> options,
        CompanyProfileRepository profileRepo,
        ServerConfigRepository serverConfigs,
        ILogger<GifSearchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? new SpokesOptions();
        _profileRepo = profileRepo ?? throw new ArgumentNullException(nameof(profileRepo));
        _serverConfigs = serverConfigs ?? throw new ArgumentNullException(nameof(serverConfigs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GifSearchResult> SearchGifsAsync(
        string query,
        int offset = 0,
        string? cursor = null,
        bool append = false,
        CancellationToken cancellationToken = default)
    {
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

        var effectiveOffset = append ? offset : 0;
        var cleanQuery = Regex.Replace(query.Trim(), @"\s+", " ");
        var escapedQuery = Uri.EscapeDataString(cleanQuery);
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            if (isDefaultProvider)
            {
                return await SearchViaRelayAsync(httpClient, cleanQuery, effectiveOffset, cancellationToken);
            }

            if (provider == "Klipy")
            {
                var page = (effectiveOffset / 20) + 1;
                var url = $"https://api.klipy.com/api/v1/{apiKey}/gifs/search?q={escapedQuery}&per_page=20&page={page}";
                var json = await httpClient.GetStringAsync(url, cancellationToken);
                return ParseKlipyJson(json, effectiveOffset);
            }

            if (provider == "Tenor")
            {
                var url = $"https://tenor.googleapis.com/v2/search?q={escapedQuery}&key={apiKey}&limit=20";
                if (append && !string.IsNullOrEmpty(cursor)) url += $"&pos={cursor}";

                var json = await httpClient.GetStringAsync(url, cancellationToken);
                return ParseTenorJson(json, effectiveOffset);
            }

            if (provider == "Giphy")
            {
                var url = $"https://api.giphy.com/v1/gifs/search?api_key={apiKey}&q={escapedQuery}&limit=20&offset={effectiveOffset}";
                var json = await httpClient.GetStringAsync(url, cancellationToken);
                return ParseGiphyJson(json, effectiveOffset);
            }

            return new GifSearchResult
            {
                HasMore = false,
                ErrorMessage = $"Unknown GIF provider: {provider}"
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error while searching GIFs with provider {Provider}", provider);
            return new GifSearchResult
            {
                HasMore = false,
                ErrorMessage = isDefaultProvider
                    ? "Spokes GIF relay is currently unreachable. Please try again later."
                    : $"GIF Provider HTTP Error: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching GIFs with provider {Provider}", provider);
            return new GifSearchResult
            {
                HasMore = false,
                ErrorMessage = "Error fetching GIFs. Please try again."
            };
        }
    }

    private async Task<GifSearchResult> SearchViaRelayAsync(
        HttpClient httpClient,
        string query,
        int offset,
        CancellationToken cancellationToken)
    {
        var config = _serverConfigs.GetOrCreateGlobalConfig();
        var fingerprint = RelayTrialTicketService.ComputeFingerprint(config.ServerMasterRsaPublicKey) ?? string.Empty;
        var page = (offset / 20) + 1;

        var profile = _profileRepo.Get();
        var relayPayload = new GifRelayPayload
        {
            Query = query,
            Page = page,
            PerPage = 20,
            ServerFingerprint = fingerprint,
            ServerId = config.DatabaseCreationId,
            LicensePayload = profile?.LicensePayload
        };

        var relayUrl = !string.IsNullOrEmpty(_options.GifRelayUrl)
            ? _options.GifRelayUrl
            : Constants.SpokesConstants.GifRelayUrl;

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, relayUrl);
        if (!string.IsNullOrEmpty(_options.PushRelayKey))
        {
            requestMessage.Headers.Add("X-Relay-Key", _options.PushRelayKey);
        }
        requestMessage.Content = JsonContent.Create(relayPayload);

        using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return new GifSearchResult
                {
                    HasMore = false,
                    ErrorMessage = "GIF search rate limit reached. Please wait a moment."
                };
            }

            _logger.LogError("GIF relay rejected request with status code {StatusCode}", response.StatusCode);
            return new GifSearchResult
            {
                HasMore = false,
                ErrorMessage = "Spokes GIF relay is currently unavailable."
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseKlipyJson(json, offset);
    }

    public static GifSearchResult ParseKlipyJson(string json, int currentOffset)
    {
        var root = JsonNode.Parse(json);
        var results = root as JsonArray
                   ?? root?["data"] as JsonArray
                   ?? root?["data"]?["data"] as JsonArray
                   ?? root?["results"] as JsonArray;

        var returnedCount = results?.Count ?? 0;
        var hasMore = returnedCount >= 20;
        var newOffset = currentOffset + returnedCount;

        var items = new List<GifItem>();
        if (results != null)
        {
            foreach (var res in results)
            {
                var files = res?["file"] ?? res?["files"];
                var gifUrl = files?["hd"]?["gif"]?["url"]?.ToString()
                          ?? files?["hd"]?["url"]?.ToString()
                          ?? res?["url"]?.ToString();

                var previewUrl = files?["preview"]?["gif"]?["url"]?.ToString()
                              ?? files?["preview"]?["url"]?.ToString()
                              ?? gifUrl;

                double? aspectRatio = null;
                var targetObj = files?["preview"]?["gif"] ?? files?["preview"] ?? files?["hd"]?["gif"] ?? res;
                if (targetObj != null)
                {
                    var wStr = targetObj["width"]?.ToString();
                    var hStr = targetObj["height"]?.ToString();
                    if (double.TryParse(wStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var w) &&
                        double.TryParse(hStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var h) && h > 0)
                    {
                        aspectRatio = w / h;
                    }
                }

                if (!string.IsNullOrEmpty(gifUrl) && !items.Any(g => g.Url == gifUrl))
                {
                    items.Add(new GifItem
                    {
                        Url = gifUrl,
                        PreviewUrl = previewUrl ?? gifUrl,
                        AspectRatio = aspectRatio
                    });
                }
            }
        }

        return new GifSearchResult
        {
            Results = items,
            HasMore = hasMore && items.Count > 0,
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
                var gifUrl = media?["gif"]?["url"]?.ToString();
                var tinyUrl = media?["tinygif"]?["url"]?.ToString();

                double? aspectRatio = null;
                var dims = media?["tinygif"]?["dims"] as JsonArray ?? media?["gif"]?["dims"] as JsonArray;
                if (dims != null && dims.Count >= 2)
                {
                    var wStr = dims[0]?.ToString();
                    var hStr = dims[1]?.ToString();
                    if (double.TryParse(wStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var w) &&
                        double.TryParse(hStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var h) && h > 0)
                    {
                        aspectRatio = w / h;
                    }
                }

                if (!string.IsNullOrEmpty(gifUrl) && !items.Any(g => g.Url == gifUrl))
                {
                    items.Add(new GifItem
                    {
                        Url = gifUrl,
                        PreviewUrl = tinyUrl ?? gifUrl,
                        AspectRatio = aspectRatio
                    });
                }
            }
        }

        return new GifSearchResult
        {
            Results = items,
            HasMore = hasMore,
            NextCursor = nextCursor,
            NewOffset = currentOffset + items.Count
        };
    }

    public static GifSearchResult ParseGiphyJson(string json, int currentOffset)
    {
        var root = JsonNode.Parse(json);
        var pagination = root?["pagination"];
        var count = pagination?["count"]?.GetValue<int>() ?? 0;
        var hasMore = count >= 20;

        var items = new List<GifItem>();
        if (root?["data"] is JsonArray results)
        {
            foreach (var res in results)
            {
                var images = res?["images"];
                var gifUrl = images?["original"]?["url"]?.ToString();
                var previewUrl = images?["fixed_height_small"]?["url"]?.ToString();

                double? aspectRatio = null;
                var targetImg = images?["fixed_height_small"] ?? images?["original"];
                if (targetImg != null)
                {
                    var wStr = targetImg["width"]?.ToString();
                    var hStr = targetImg["height"]?.ToString();
                    if (double.TryParse(wStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var w) &&
                        double.TryParse(hStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var h) && h > 0)
                    {
                        aspectRatio = w / h;
                    }
                }

                if (!string.IsNullOrEmpty(gifUrl) && !items.Any(g => g.Url == gifUrl))
                {
                    items.Add(new GifItem
                    {
                        Url = gifUrl,
                        PreviewUrl = previewUrl ?? gifUrl,
                        AspectRatio = aspectRatio
                    });
                }
            }
        }

        return new GifSearchResult
        {
            Results = items,
            HasMore = hasMore,
            NewOffset = currentOffset + count
        };
    }
}
