using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public record GitHubRepoItem(string FullName, bool Private, string DefaultBranch);

public record GitHubTokenVerificationResult(bool Success, string? Username, string? ErrorMessage = null);

public class GitHubBackupProvider : IBackupDestinationProvider
{
    private const string GitHubApiBaseUrl = "https://api.github.com";
    private const long MaxGitHubBlobSizeBytes = 100L * 1024 * 1024; // 100 MB GitHub file limit
    internal const int MaxInlineBlobSizeBytes = 256 * 1024; // 256 KB max for inline UTF-8 content in POST /git/trees

    private readonly CompanyProfileRepository _profiles;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _dataPath;
    private readonly EncryptionService _encryptionService;
    private readonly ILogger<GitHubBackupProvider>? _logger;

    public BackupDestinationType DestinationType => BackupDestinationType.GitHub;

    /// <summary>
    /// Maximum number of tree entries sent in a single POST /repos/{owner}/{repo}/git/trees request.
    /// Keeps payload sizes well below GitHub reverse-proxy timeout thresholds.
    /// </summary>
    public int MaxEntriesPerTreeBatch { get; init; } = 150;

    /// <summary>
    /// Maximum cumulative inline UTF-8 content bytes sent in a single POST /repos/{owner}/{repo}/git/trees request (~1.5 MB).
    /// </summary>
    public long MaxInlineBytesPerTreeBatch { get; init; } = 1_500_000;

    /// <summary>
    /// Maximum number of binary POST /git/blobs uploads grouped before flushing a tree batch.
    /// </summary>
    public int MaxBinaryBlobsPerBatch { get; init; } = 25;

    /// <summary>
    /// Number of tree batches after which an intermediate commit and branch ref update is checkpointed.
    /// Guarantees large initial backups persist progress incrementally on the remote branch.
    /// </summary>
    public int CommitCheckpointIntervalBatches { get; init; } = 3;

    /// <summary>
    /// Minimum delay between consecutive mutating POST /git/blobs requests to stay below GitHub's 80 mutating requests/min secondary rate limit.
    /// </summary>
    public TimeSpan BlobPostPacingDelay { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Maximum retry attempts when GitHub returns HTTP 403 (secondary rate limit), HTTP 429, or transient 502/503/504 errors.
    /// </summary>
    public int MaxRateLimitRetries { get; init; } = 3;

    /// <summary>
    /// Base backoff delay when GitHub returns a secondary rate limit without an explicit Retry-After header.
    /// </summary>
    public TimeSpan DefaultSecondaryRateLimitDelay { get; init; } = TimeSpan.FromSeconds(15);

    public GitHubBackupProvider(
        CompanyProfileRepository profiles,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        EncryptionService? encryptionService = null,
        ILogger<GitHubBackupProvider>? logger = null)
    {
        _profiles = profiles;
        _httpClientFactory = httpClientFactory;
        _dataPath = configuration["DataPath"] ?? "Data";
        _encryptionService = encryptionService ?? profiles?.EncryptionService ?? new EncryptionService(configuration);
        _logger = logger;
    }

    private string? ResolveStoredToken(string? tokenOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(tokenOverride)) return tokenOverride;
        var stored = _profiles.Get()?.Backup.GitHub.PersonalAccessToken;
        if (string.IsNullOrWhiteSpace(stored)) return null;
        return _encryptionService.TryDecrypt(stored, out var decrypted) ? decrypted : stored;
    }

    internal static string NormalizeTargetFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return string.Empty;
        var normalized = folder.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }
        normalized = normalized.Trim('/');
        return normalized;
    }

    internal static string CombineRepoPath(string? folder, string relativePath)
    {
        var cleanFolder = NormalizeTargetFolder(folder);
        var cleanRel = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        while (cleanRel.Contains("//", StringComparison.Ordinal))
        {
            cleanRel = cleanRel.Replace("//", "/", StringComparison.Ordinal);
        }
        return string.IsNullOrEmpty(cleanFolder) ? cleanRel : $"{cleanFolder}/{cleanRel}";
    }

    internal static bool TryParseRepository(string? repository, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(repository)) return false;

        var parts = repository.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        owner = parts[0];
        repo = parts[1];
        return true;
    }

    internal static async Task<string> ComputeGitBlobSha1Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var headerBytes = Encoding.ASCII.GetBytes($"blob {fileStream.Length}\0");
        sha1.AppendData(headerBytes);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            sha1.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }

    /// <summary>
    /// Determines whether raw file bytes can be passed directly as inline UTF-8 "content" in POST /git/trees
    /// without altering the byte representation or resulting Git blob SHA-1.
    /// </summary>
    internal static bool TryDecodeInlineUtf8Content(byte[] fileBytes, out string? utf8Content)
    {
        utf8Content = null;
        if (fileBytes == null || fileBytes.Length > MaxInlineBlobSizeBytes)
        {
            return false;
        }

        // Upload UTF-8 BOM files via base64 POST /git/blobs so BOM bytes are preserved unambiguously
        if (fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF)
        {
            return false;
        }

        // Binary files contain null bytes
        if (fileBytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            return false;
        }

        if (!Utf8.IsValid(fileBytes))
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(fileBytes);
        if (Encoding.UTF8.GetByteCount(decoded) != fileBytes.Length)
        {
            return false;
        }

        utf8Content = decoded;
        return true;
    }

    private HttpClient CreateGitHubClient(string token)
    {
        var client = _httpClientFactory.CreateClient("GitHubBackup");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Spokes-Server", "1.0"));
        if (!client.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
        {
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        return client;
    }

    private async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> requestFactory,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await requestFactory(cancellationToken);
            if (response.IsSuccessStatusCode || attempt >= MaxRateLimitRetries)
            {
                return response;
            }

            if (!await IsRetryableGitHubFailureAsync(response, cancellationToken))
            {
                return response;
            }

            var delay = ResolveRetryDelay(response, attempt);
            attempt++;

            _logger?.LogWarning(
                "[GitHubBackup] GitHub returned HTTP {StatusCode} (rate limit or transient gateway error). Waiting {DelaySeconds:F1}s before retry {Attempt}/{MaxRetries}...",
                (int)response.StatusCode, delay.TotalSeconds, attempt, MaxRateLimitRetries);

            response.Dispose();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static async Task<bool> IsRetryableGitHubFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests ||
            status == HttpStatusCode.BadGateway ||
            status == HttpStatusCode.ServiceUnavailable ||
            status == HttpStatusCode.GatewayTimeout)
        {
            return true;
        }

        if (status == HttpStatusCode.Forbidden)
        {
            if (response.Headers.RetryAfter != null)
            {
                return true;
            }

            if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remainingValues) &&
                remainingValues.Any(v => string.Equals(v.Trim(), "0", StringComparison.Ordinal)))
            {
                return true;
            }

            if (response.Content != null)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("abuse detection", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter != null)
        {
            if (retryAfter.Delta.HasValue)
            {
                var delta = retryAfter.Delta.Value;
                return delta < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(120, delta.TotalSeconds));
            }

            if (retryAfter.Date.HasValue)
            {
                var wait = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return wait < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(120, wait.TotalSeconds));
            }
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var resetValues))
        {
            var firstReset = resetValues.FirstOrDefault();
            if (long.TryParse(firstReset, out var resetEpochSeconds))
            {
                var resetTime = DateTimeOffset.FromUnixTimeSeconds(resetEpochSeconds);
                var wait = resetTime - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                if (wait > TimeSpan.Zero)
                {
                    return TimeSpan.FromSeconds(Math.Min(120, wait.TotalSeconds));
                }
            }
        }

        var multiplier = Math.Pow(2, attempt);
        var calculatedMs = DefaultSecondaryRateLimitDelay.TotalMilliseconds * multiplier;
        return TimeSpan.FromMilliseconds(Math.Min(120_000, Math.Max(0, calculatedMs)));
    }

    public async Task<GitHubTokenVerificationResult> VerifyTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new GitHubTokenVerificationResult(false, null, "Personal Access Token cannot be empty.");
        }

        try
        {
            var client = CreateGitHubClient(token);
            using var response = await SendWithRateLimitRetryAsync(
                ct => client.GetAsync($"{GitHubApiBaseUrl}/user", ct),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubTokenVerificationResult(false, null, $"GitHub authentication failed (HTTP {(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var login = doc.RootElement.TryGetProperty("login", out var loginProp) ? loginProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(login))
            {
                return new GitHubTokenVerificationResult(false, null, "Could not resolve GitHub username from token.");
            }

            return new GitHubTokenVerificationResult(true, login);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GitHubBackup] Token verification failed");
            return new GitHubTokenVerificationResult(false, null, ex.Message);
        }
    }

    public async Task<List<GitHubRepoItem>> ListRepositoriesAsync(string? tokenOverride = null, CancellationToken cancellationToken = default)
    {
        var token = ResolveStoredToken(tokenOverride);
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        try
        {
            var client = CreateGitHubClient(token);
            var url = $"{GitHubApiBaseUrl}/user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member";
            using var response = await SendWithRateLimitRetryAsync(
                ct => client.GetAsync(url, ct),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var repos = new List<GitHubRepoItem>();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var fullName = item.TryGetProperty("full_name", out var fnProp) ? fnProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(fullName)) continue;

                    bool isPrivate = item.TryGetProperty("private", out var privProp) && privProp.ValueKind == JsonValueKind.True;
                    var defaultBranch = item.TryGetProperty("default_branch", out var dbProp) ? dbProp.GetString() ?? "main" : "main";
                    repos.Add(new GitHubRepoItem(fullName, isPrivate, defaultBranch));
                }
            }

            return repos;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GitHubBackup] Failed to list repositories");
            return [];
        }
    }

    public async Task<List<string>> ListRepositoryFoldersAsync(string repository, string? tokenOverride = null, CancellationToken cancellationToken = default)
    {
        var token = ResolveStoredToken(tokenOverride);
        if (string.IsNullOrWhiteSpace(token) || !TryParseRepository(repository, out var owner, out var repo))
        {
            return [];
        }

        try
        {
            var client = CreateGitHubClient(token);
            var (defaultBranch, _, repoError) = await GetRepositoryMetadataAsync(client, owner, repo, cancellationToken);
            if (defaultBranch == null || repoError != null)
            {
                return [];
            }

            var treeUrl = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/git/trees/{Uri.EscapeDataString(defaultBranch)}?recursive=1";
            using var treeRes = await SendWithRateLimitRetryAsync(
                ct => client.GetAsync(treeUrl, ct),
                cancellationToken);
            if (!treeRes.IsSuccessStatusCode)
            {
                // 404 or 409 occurs when the repository is brand new / empty (0 commits)
                return [];
            }

            using var doc = await JsonDocument.ParseAsync(await treeRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (doc.RootElement.TryGetProperty("tree", out var treeArray) && treeArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in treeArray.EnumerateArray())
                {
                    var type = node.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                    var path = node.TryGetProperty("path", out var pProp) ? pProp.GetString() : null;
                    if (string.Equals(type, "tree", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(path))
                    {
                        folders.Add(path);
                    }
                }
            }

            return folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GitHubBackup] Failed to list folders for repository {Repo}", repository);
            return [];
        }
    }

    private async Task<(string? DefaultBranch, bool HasPushPermission, string? ErrorMessage)> GetRepositoryMetadataAsync(
        HttpClient client,
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        using var repoRes = await SendWithRateLimitRetryAsync(
            ct => client.GetAsync($"{GitHubApiBaseUrl}/repos/{owner}/{repo}", ct),
            cancellationToken);
        if (!repoRes.IsSuccessStatusCode)
        {
            return (null, false, $"Repository '{owner}/{repo}' could not be accessed (HTTP {(int)repoRes.StatusCode} {repoRes.ReasonPhrase}).");
        }

        using var doc = await JsonDocument.ParseAsync(await repoRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var defaultBranch = doc.RootElement.TryGetProperty("default_branch", out var dbProp) && !string.IsNullOrWhiteSpace(dbProp.GetString())
            ? dbProp.GetString()!
            : "main";

        bool hasPush = true;
        if (doc.RootElement.TryGetProperty("permissions", out var perms) &&
            perms.ValueKind == JsonValueKind.Object &&
            perms.TryGetProperty("push", out var pushProp))
        {
            hasPush = pushProp.ValueKind == JsonValueKind.True;
        }

        return (defaultBranch, hasPush, null);
    }

    private async Task<string?> GetBranchHeadCommitShaAsync(
        HttpClient client,
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken)
    {
        var refUrl = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/git/ref/heads/{Uri.EscapeDataString(branch)}";
        using var refRes = await SendWithRateLimitRetryAsync(
            ct => client.GetAsync(refUrl, ct),
            cancellationToken);
        if (refRes.StatusCode == HttpStatusCode.NotFound || refRes.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }

        if (!refRes.IsSuccessStatusCode)
        {
            var err = await refRes.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to query branch '{branch}' ref (HTTP {(int)refRes.StatusCode}): {err}");
        }

        using var doc = await JsonDocument.ParseAsync(await refRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (doc.RootElement.TryGetProperty("object", out var objProp) &&
            objProp.TryGetProperty("sha", out var shaProp))
        {
            return shaProp.GetString();
        }

        return null;
    }

    private async Task<string> BootstrapEmptyRepositoryAsync(
        HttpClient client,
        string owner,
        string repo,
        string branch,
        string normalizedFolder,
        CancellationToken cancellationToken)
    {
        var initPath = CombineRepoPath(normalizedFolder, ".gitkeep");
        var contentsUrl = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/contents/{initPath}";
        var payload = new
        {
            message = "Initialize Spokes backup repository",
            content = "",
            branch
        };

        using var res = await SendWithRateLimitRetryAsync(
            ct => client.PutAsJsonAsync(contentsUrl, payload, ct),
            cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to initialize empty GitHub repository (HTTP {(int)res.StatusCode}): {err}");
        }

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (doc.RootElement.TryGetProperty("commit", out var commitProp) &&
            commitProp.TryGetProperty("sha", out var shaProp) &&
            !string.IsNullOrWhiteSpace(shaProp.GetString()))
        {
            return shaProp.GetString()!;
        }

        throw new InvalidOperationException("GitHub did not return a commit SHA after initializing the repository.");
    }

    private async Task<Dictionary<string, string>> GetRemoteFolderBlobsAsync(
        HttpClient client,
        string owner,
        string repo,
        string commitSha,
        string normalizedFolder,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var treeUrl = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/git/trees/{commitSha}?recursive=1";
        using var res = await SendWithRateLimitRetryAsync(
            ct => client.GetAsync(treeUrl, ct),
            cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            return result;
        }

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("tree", out var treeProp) || treeProp.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        bool isTruncated = doc.RootElement.TryGetProperty("truncated", out var truncProp) &&
                           truncProp.ValueKind == JsonValueKind.True;

        var folderPrefix = string.IsNullOrEmpty(normalizedFolder) ? string.Empty : normalizedFolder + "/";
        string? targetFolderSubtreeSha = null;

        foreach (var item in treeProp.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
            var path = item.TryGetProperty("path", out var pProp) ? pProp.GetString() : null;
            var sha = item.TryGetProperty("sha", out var sProp) ? sProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha)) continue;

            if (!string.IsNullOrEmpty(normalizedFolder) &&
                string.Equals(type, "tree", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(path, normalizedFolder, StringComparison.Ordinal))
            {
                targetFolderSubtreeSha = sha;
            }

            if (string.Equals(type, "blob", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(folderPrefix) || path.StartsWith(folderPrefix, StringComparison.Ordinal))
                {
                    result[path] = sha;
                }
            }
        }

        // If the root recursive tree was truncated by GitHub (>100k entries or >7 MB across the whole repo),
        // resolve and traverse the target folder subtree directly so no existing files inside TargetFolder are missed.
        if (isTruncated && !string.IsNullOrEmpty(normalizedFolder))
        {
            targetFolderSubtreeSha ??= await ResolveSubtreeShaByPathAsync(client, owner, repo, commitSha, normalizedFolder, cancellationToken);
            if (!string.IsNullOrEmpty(targetFolderSubtreeSha))
            {
                await PopulateSubtreeBlobsAsync(client, owner, repo, targetFolderSubtreeSha, normalizedFolder, result, cancellationToken);
            }
        }

        return result;
    }

    private async Task<string?> ResolveSubtreeShaByPathAsync(
        HttpClient client,
        string owner,
        string repo,
        string rootTreeOrCommitSha,
        string normalizedFolder,
        CancellationToken cancellationToken)
    {
        var segments = normalizedFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentSha = rootTreeOrCommitSha;

        foreach (var segment in segments)
        {
            var url = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/git/trees/{currentSha}";
            using var res = await SendWithRateLimitRetryAsync(ct => client.GetAsync(url, ct), cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("tree", out var treeProp) || treeProp.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? nextSha = null;
            foreach (var item in treeProp.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                var path = item.TryGetProperty("path", out var pProp) ? pProp.GetString() : null;
                var sha = item.TryGetProperty("sha", out var sProp) ? sProp.GetString() : null;
                if (string.Equals(type, "tree", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(path, segment, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(sha))
                {
                    nextSha = sha;
                    break;
                }
            }

            if (string.IsNullOrEmpty(nextSha))
            {
                return null;
            }

            currentSha = nextSha;
        }

        return currentSha;
    }

    private async Task PopulateSubtreeBlobsAsync(
        HttpClient client,
        string owner,
        string repo,
        string subtreeSha,
        string prefixPath,
        Dictionary<string, string> result,
        CancellationToken cancellationToken)
    {
        var url = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/git/trees/{subtreeSha}?recursive=1";
        using var res = await SendWithRateLimitRetryAsync(ct => client.GetAsync(url, ct), cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            return;
        }

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("tree", out var treeProp) || treeProp.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in treeProp.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
            var path = item.TryGetProperty("path", out var pProp) ? pProp.GetString() : null;
            var sha = item.TryGetProperty("sha", out var sProp) ? sProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha)) continue;

            if (string.Equals(type, "blob", StringComparison.OrdinalIgnoreCase))
            {
                var fullPath = CombineRepoPath(prefixPath, path);
                result[fullPath] = sha;
            }
        }
    }

    [JsonConverter(typeof(GitTreeEntryPayloadConverter))]
    internal sealed class GitTreeEntryPayload
    {
        public required string Path { get; init; }
        public string Mode { get; init; } = "100644";
        public string Type { get; init; } = "blob";
        public string? Sha { get; init; }
        public string? Content { get; init; }
        public bool IsInlineContent => Content is not null;
    }

    private sealed class GitTreeEntryPayloadConverter : JsonConverter<GitTreeEntryPayload>
    {
        public override GitTreeEntryPayload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, GitTreeEntryPayload value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("path", value.Path);
            writer.WriteString("mode", value.Mode);
            writer.WriteString("type", value.Type);
            if (value.IsInlineContent)
            {
                writer.WriteString("content", value.Content);
            }
            else if (value.Sha is null)
            {
                writer.WriteNull("sha");
            }
            else
            {
                writer.WriteString("sha", value.Sha);
            }
            writer.WriteEndObject();
        }
    }

    public async Task<BackupUploadResult> UploadAsync(
        string localBackupPath,
        string remoteFileName,
        IProgress<BackupUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profile = _profiles.Get();
        var config = profile?.Backup.GitHub;
        var token = ResolveStoredToken();
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(token))
        {
            return BackupUploadResult.Failed("GitHub backup is not enabled or Personal Access Token is missing.");
        }

        if (!TryParseRepository(config.Repository, out var owner, out var repo))
        {
            return BackupUploadResult.Failed("GitHub repository must be configured in 'owner/repo' format.");
        }

        try
        {
            var (candidateFiles, totalBytes) = BackupFileScanner.ScanCandidateFiles(
                _dataPath,
                profile?.BackupEmployeeEmails ?? false);

            foreach (var (file, relPath) in candidateFiles)
            {
                if (file.Length > MaxGitHubBlobSizeBytes)
                {
                    var sizeMb = file.Length / (1024.0 * 1024.0);
                    return BackupUploadResult.Failed(
                        $"File '{relPath}' ({sizeMb:F1} MB) exceeds GitHub's 100 MB per-file repository limit.");
                }
            }

            progress?.Report(new BackupUploadProgress(0, totalBytes, 0));

            var client = CreateGitHubClient(token);
            var (defaultBranch, hasPush, metaErr) = await GetRepositoryMetadataAsync(client, owner, repo, cancellationToken);
            if (defaultBranch == null || metaErr != null)
            {
                return BackupUploadResult.Failed(metaErr ?? "Could not resolve GitHub repository metadata.");
            }

            if (!hasPush)
            {
                return BackupUploadResult.Failed($"Token does not have write/push permissions for repository '{owner}/{repo}'.");
            }

            var normalizedFolder = NormalizeTargetFolder(config.TargetFolder);
            var headCommitSha = await GetBranchHeadCommitShaAsync(client, owner, repo, defaultBranch, cancellationToken);
            if (string.IsNullOrEmpty(headCommitSha))
            {
                headCommitSha = await BootstrapEmptyRepositoryAsync(client, owner, repo, defaultBranch, normalizedFolder, cancellationToken);
            }

            var existingRemoteBlobs = await GetRemoteFolderBlobsAsync(client, owner, repo, headCommitSha, normalizedFolder, cancellationToken);
            var seenRepoPaths = new HashSet<string>(StringComparer.Ordinal);
            var pendingFileUploads = new List<(FileInfo File, string RelativePath, string RepoPath)>();
            long completedBytes = 0;

            foreach (var (file, relPath) in candidateFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!file.Exists) continue;

                var repoPath = CombineRepoPath(normalizedFolder, relPath);
                seenRepoPaths.Add(repoPath);

                var localSha = await ComputeGitBlobSha1Async(file.FullName, cancellationToken);
                if (existingRemoteBlobs.TryGetValue(repoPath, out var remoteSha) &&
                    string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase))
                {
                    completedBytes += file.Length;
                    double skippedPct = totalBytes > 0 ? Math.Min(100.0, (double)completedBytes / totalBytes * 100.0) : 100.0;
                    progress?.Report(new BackupUploadProgress(completedBytes, totalBytes, skippedPct));
                    continue;
                }

                pendingFileUploads.Add((file, relPath, repoPath));
            }

            // Mark any remote files inside the target folder that no longer exist locally for deletion (sha: null)
            var pendingDeletions = new List<string>();
            foreach (var existingPath in existingRemoteBlobs.Keys)
            {
                if (!seenRepoPaths.Contains(existingPath))
                {
                    pendingDeletions.Add(existingPath);
                }
            }

            int totalChangesCount = pendingFileUploads.Count + pendingDeletions.Count;
            if (totalChangesCount == 0)
            {
                progress?.Report(new BackupUploadProgress(totalBytes, totalBytes, 100.0));
                _logger?.LogInformation("[GitHubBackup] All {Count} files in '{Repo}/{Folder}' are already up to date at commit {Commit}.",
                    candidateFiles.Count, config.Repository, normalizedFolder, headCommitSha);
                return BackupUploadResult.Succeeded(headCommitSha, totalBytes);
            }

            int maxEntriesPerBatch = Math.Max(1, MaxEntriesPerTreeBatch);
            long maxInlineBytesPerBatch = Math.Max(1024, MaxInlineBytesPerTreeBatch);
            int maxBinaryPerBatch = Math.Max(1, MaxBinaryBlobsPerBatch);
            int checkpointInterval = Math.Max(1, CommitCheckpointIntervalBatches);

            string currentCommitSha = headCommitSha;
            string currentBaseTreeSha = headCommitSha;
            string? uncommittedTreeSha = null;
            int uncommittedBatches = 0;

            var currentBatch = new List<GitTreeEntryPayload>();
            long currentBatchInlineBytes = 0;
            long currentBatchFileBytes = 0;
            int currentBatchBinaryCount = 0;
            DateTimeOffset lastBlobPostAt = DateTimeOffset.MinValue;

            async Task<(bool Success, string? ErrorMessage)> FlushCurrentBatchAsync(bool isFinalBatch)
            {
                if (currentBatch.Count == 0)
                {
                    if (isFinalBatch && !string.IsNullOrEmpty(uncommittedTreeSha))
                    {
                        var (commitOk, newCommit, commitErr) = await CommitAndAdvanceBranchRefAsync(
                            client, owner, repo, defaultBranch, currentCommitSha, uncommittedTreeSha, cancellationToken);
                        if (!commitOk || string.IsNullOrEmpty(newCommit))
                        {
                            return (false, commitErr);
                        }
                        currentCommitSha = newCommit;
                        currentBaseTreeSha = newCommit;
                        uncommittedTreeSha = null;
                        uncommittedBatches = 0;
                    }
                    return (true, null);
                }

                long batchBytes = currentBatchFileBytes;
                long baseCompletedBeforeTree = completedBytes;
                var createTreePayload = new
                {
                    base_tree = currentBaseTreeSha,
                    tree = currentBatch
                };
                var treePayloadJson = JsonSerializer.SerializeToUtf8Bytes(createTreePayload);

                using var createTreeRes = await SendWithRateLimitRetryAsync(async ct =>
                {
                    var payloadStream = new MemoryStream(treePayloadJson);
                    var progressStream = new ProgressStream(payloadStream, treePayloadJson.Length, sentPayloadBytes =>
                    {
                        if (batchBytes <= 0) return;
                        long proportionalBytes = treePayloadJson.Length > 0
                            ? (long)((double)sentPayloadBytes / treePayloadJson.Length * batchBytes)
                            : batchBytes;
                        long currentTotal = Math.Min(totalBytes, baseCompletedBeforeTree + proportionalBytes);
                        double pct = totalBytes > 0 ? Math.Min(99.0, (double)currentTotal / totalBytes * 100.0) : 99.0;
                        progress?.Report(new BackupUploadProgress(currentTotal, totalBytes, pct));
                    });

                    var treeContent = new StreamContent(progressStream, 81920);
                    treeContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    return await client.PostAsync($"{GitHubApiBaseUrl}/repos/{owner}/{repo}/git/trees", treeContent, ct);
                }, cancellationToken);

                if (!createTreeRes.IsSuccessStatusCode)
                {
                    var err = await createTreeRes.Content.ReadAsStringAsync(cancellationToken);
                    await TryCommitCheckpointOnFailureAsync(client, owner, repo, defaultBranch, currentCommitSha, uncommittedTreeSha, cancellationToken);
                    return (false, $"Failed to create Git tree (HTTP {(int)createTreeRes.StatusCode}): {err}");
                }

                using var treeDoc = await JsonDocument.ParseAsync(await createTreeRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var newTreeSha = treeDoc.RootElement.GetProperty("sha").GetString()!;

                currentBaseTreeSha = newTreeSha;
                uncommittedTreeSha = newTreeSha;
                uncommittedBatches++;

                completedBytes += batchBytes;
                double batchPct = totalBytes > 0 ? Math.Min(99.0, (double)completedBytes / totalBytes * 100.0) : 99.0;
                progress?.Report(new BackupUploadProgress(completedBytes, totalBytes, batchPct));

                currentBatch.Clear();
                currentBatchInlineBytes = 0;
                currentBatchFileBytes = 0;
                currentBatchBinaryCount = 0;

                if (isFinalBatch || uncommittedBatches >= checkpointInterval)
                {
                    var (commitOk, newCommit, commitErr) = await CommitAndAdvanceBranchRefAsync(
                        client, owner, repo, defaultBranch, currentCommitSha, uncommittedTreeSha, cancellationToken);
                    if (!commitOk || string.IsNullOrEmpty(newCommit))
                    {
                        return (false, commitErr);
                    }

                    currentCommitSha = newCommit;
                    currentBaseTreeSha = newCommit;
                    uncommittedTreeSha = null;
                    uncommittedBatches = 0;
                }

                return (true, null);
            }

            foreach (var (file, relPath, repoPath) in pendingFileUploads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!file.Exists) continue;

                byte[] fileBytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
                long fileLength = fileBytes.Length;

                if (TryDecodeInlineUtf8Content(fileBytes, out var inlineUtf8))
                {
                    long inlineByteCount = fileBytes.Length;
                    if (currentBatch.Count > 0 &&
                        (currentBatch.Count >= maxEntriesPerBatch ||
                         currentBatchInlineBytes + inlineByteCount > maxInlineBytesPerBatch))
                    {
                        var (flushOk, flushErr) = await FlushCurrentBatchAsync(isFinalBatch: false);
                        if (!flushOk)
                        {
                            return BackupUploadResult.Failed(flushErr ?? "Failed to flush Git tree batch.");
                        }
                    }

                    currentBatch.Add(new GitTreeEntryPayload
                    {
                        Path = repoPath,
                        Mode = "100644",
                        Type = "blob",
                        Content = inlineUtf8
                    });
                    currentBatchInlineBytes += inlineByteCount;
                    currentBatchFileBytes += fileLength;
                }
                else
                {
                    if (currentBatch.Count > 0 &&
                        (currentBatch.Count >= maxEntriesPerBatch ||
                         currentBatchBinaryCount >= maxBinaryPerBatch))
                    {
                        var (flushOk, flushErr) = await FlushCurrentBatchAsync(isFinalBatch: false);
                        if (!flushOk)
                        {
                            return BackupUploadResult.Failed(flushErr ?? "Failed to flush Git tree batch.");
                        }
                    }

                    // Polite pacing between consecutive binary POST /git/blobs requests
                    if (lastBlobPostAt != DateTimeOffset.MinValue && BlobPostPacingDelay > TimeSpan.Zero)
                    {
                        var elapsed = DateTimeOffset.UtcNow - lastBlobPostAt;
                        if (elapsed < BlobPostPacingDelay)
                        {
                            await Task.Delay(BlobPostPacingDelay - elapsed, cancellationToken);
                        }
                    }

                    long baseCompleted = completedBytes + currentBatchFileBytes;
                    var blobPayloadJson = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        content = Convert.ToBase64String(fileBytes),
                        encoding = "base64"
                    });

                    using var blobRes = await SendWithRateLimitRetryAsync(async ct =>
                    {
                        var payloadStream = new MemoryStream(blobPayloadJson);
                        var progressStream = new ProgressStream(payloadStream, blobPayloadJson.Length, sentPayloadBytes =>
                        {
                            long proportionalFileBytes = blobPayloadJson.Length > 0
                                ? (long)((double)sentPayloadBytes / blobPayloadJson.Length * fileLength)
                                : fileLength;
                            long currentTotal = Math.Min(totalBytes, baseCompleted + proportionalFileBytes);
                            double pct = totalBytes > 0 ? Math.Min(99.0, (double)currentTotal / totalBytes * 100.0) : 99.0;
                            progress?.Report(new BackupUploadProgress(currentTotal, totalBytes, pct));
                        });

                        var blobContent = new StreamContent(progressStream, 81920);
                        blobContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        return await client.PostAsync($"{GitHubApiBaseUrl}/repos/{owner}/{repo}/git/blobs", blobContent, ct);
                    }, cancellationToken);

                    lastBlobPostAt = DateTimeOffset.UtcNow;

                    if (!blobRes.IsSuccessStatusCode)
                    {
                        var err = await blobRes.Content.ReadAsStringAsync(cancellationToken);
                        if (currentBatch.Count > 0)
                        {
                            // Flush any already-uploaded entries in the current batch before checkpointing
                            await FlushCurrentBatchAsync(isFinalBatch: true);
                        }
                        else
                        {
                            await TryCommitCheckpointOnFailureAsync(client, owner, repo, defaultBranch, currentCommitSha, uncommittedTreeSha, cancellationToken);
                        }
                        return BackupUploadResult.Failed($"Failed to upload blob for '{relPath}' (HTTP {(int)blobRes.StatusCode}): {err}");
                    }

                    using var blobDoc = await JsonDocument.ParseAsync(await blobRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                    var createdSha = blobDoc.RootElement.GetProperty("sha").GetString()!;

                    currentBatch.Add(new GitTreeEntryPayload
                    {
                        Path = repoPath,
                        Mode = "100644",
                        Type = "blob",
                        Sha = createdSha
                    });
                    currentBatchBinaryCount++;
                    completedBytes += fileLength;

                    double filePct = totalBytes > 0 ? Math.Min(99.0, (double)(completedBytes + currentBatchFileBytes) / totalBytes * 100.0) : 99.0;
                    progress?.Report(new BackupUploadProgress(completedBytes + currentBatchFileBytes, totalBytes, filePct));
                }
            }

            foreach (var deletedPath in pendingDeletions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (currentBatch.Count >= maxEntriesPerBatch)
                {
                    var (flushOk, flushErr) = await FlushCurrentBatchAsync(isFinalBatch: false);
                    if (!flushOk)
                    {
                        return BackupUploadResult.Failed(flushErr ?? "Failed to flush Git tree batch.");
                    }
                }

                currentBatch.Add(new GitTreeEntryPayload
                {
                    Path = deletedPath,
                    Mode = "100644",
                    Type = "blob",
                    Sha = null
                });
            }

            var (finalFlushOk, finalFlushErr) = await FlushCurrentBatchAsync(isFinalBatch: true);
            if (!finalFlushOk)
            {
                return BackupUploadResult.Failed(finalFlushErr ?? "Failed to finalize Git backup commit.");
            }

            progress?.Report(new BackupUploadProgress(totalBytes, totalBytes, 100.0));
            _logger?.LogInformation(
                "[GitHubBackup] Committed {ChangedCount} file change(s) across {TotalCount} files to {Owner}/{Repo} ({Folder}) at commit {Sha}",
                totalChangesCount, candidateFiles.Count, owner, repo, normalizedFolder, currentCommitSha);

            return BackupUploadResult.Succeeded(currentCommitSha, totalBytes);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[GitHubBackup] Backup sync failed for repository {Repo}", config.Repository);
            return BackupUploadResult.Failed(ex.Message);
        }
    }

    private async Task<(bool Success, string? CommitSha, string? ErrorMessage)> CommitAndAdvanceBranchRefAsync(
        HttpClient client,
        string owner,
        string repo,
        string defaultBranch,
        string parentCommitSha,
        string treeSha,
        CancellationToken cancellationToken)
    {
        var commitMessage = $"Spokes backup ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC)";
        var createCommitPayload = new
        {
            message = commitMessage,
            tree = treeSha,
            parents = new[] { parentCommitSha }
        };

        using var createCommitRes = await SendWithRateLimitRetryAsync(
            ct => client.PostAsJsonAsync($"{GitHubApiBaseUrl}/repos/{owner}/{repo}/git/commits", createCommitPayload, ct),
            cancellationToken);
        if (!createCommitRes.IsSuccessStatusCode)
        {
            var err = await createCommitRes.Content.ReadAsStringAsync(cancellationToken);
            return (false, null, $"Failed to create Git commit (HTTP {(int)createCommitRes.StatusCode}): {err}");
        }

        using var commitDoc = await JsonDocument.ParseAsync(await createCommitRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var newCommitSha = commitDoc.RootElement.GetProperty("sha").GetString()!;

        var updateRefPayload = new
        {
            sha = newCommitSha,
            force = false
        };
        using var updateRefRes = await SendWithRateLimitRetryAsync(ct =>
        {
            var updateRefReq = new HttpRequestMessage(
                HttpMethod.Patch,
                $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/git/refs/heads/{Uri.EscapeDataString(defaultBranch)}")
            {
                Content = JsonContent.Create(updateRefPayload)
            };
            return client.SendAsync(updateRefReq, ct);
        }, cancellationToken);

        if (!updateRefRes.IsSuccessStatusCode)
        {
            var err = await updateRefRes.Content.ReadAsStringAsync(cancellationToken);
            return (false, null, $"Failed to update branch '{defaultBranch}' ref (HTTP {(int)updateRefRes.StatusCode}): {err}");
        }

        return (true, newCommitSha, null);
    }

    private async Task TryCommitCheckpointOnFailureAsync(
        HttpClient client,
        string owner,
        string repo,
        string defaultBranch,
        string parentCommitSha,
        string? uncommittedTreeSha,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(uncommittedTreeSha) ||
            string.Equals(uncommittedTreeSha, parentCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var (ok, commitSha, _) = await CommitAndAdvanceBranchRefAsync(
                client, owner, repo, defaultBranch, parentCommitSha, uncommittedTreeSha, cancellationToken);
            if (ok)
            {
                _logger?.LogInformation(
                    "[GitHubBackup] Saved partial checkpoint commit {CommitSha} on '{Branch}' so completed batches do not need to be re-uploaded on retry.",
                    commitSha, defaultBranch);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GitHubBackup] Could not save partial checkpoint commit on failure");
        }
    }

    public async Task<List<RemoteBackupItem>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.GitHub;
        var token = ResolveStoredToken();
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        if (!TryParseRepository(config.Repository, out var owner, out var repo))
        {
            return [];
        }

        try
        {
            var client = CreateGitHubClient(token);
            var normalizedFolder = NormalizeTargetFolder(config.TargetFolder);
            var pathQuery = string.IsNullOrEmpty(normalizedFolder) ? string.Empty : $"&path={Uri.EscapeDataString(normalizedFolder)}";
            var commitsUrl = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/commits?per_page=20{pathQuery}";

            using var res = await SendWithRateLimitRetryAsync(
                ct => client.GetAsync(commitsUrl, ct),
                cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                return [];
            }

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var items = new List<RemoteBackupItem>();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    var sha = c.TryGetProperty("sha", out var sProp) ? sProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(sha)) continue;

                    DateTime created = DateTime.UtcNow;
                    string message = sha[..Math.Min(7, sha.Length)];

                    if (c.TryGetProperty("commit", out var commitObj))
                    {
                        if (commitObj.TryGetProperty("message", out var msgProp) && !string.IsNullOrWhiteSpace(msgProp.GetString()))
                        {
                            message = msgProp.GetString()!.Split('\n')[0].Trim();
                        }
                        if (commitObj.TryGetProperty("committer", out var committerObj) &&
                            committerObj.TryGetProperty("date", out var dateProp) &&
                            DateTime.TryParse(dateProp.GetString(), out var parsedDate))
                        {
                            created = parsedDate.ToUniversalTime();
                        }
                    }

                    items.Add(new RemoteBackupItem(sha, message, 0, created, "GitHub"));
                }
            }

            return items;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GitHubBackup] Failed to list backup commits");
            return [];
        }
    }

    public async Task<Stream?> DownloadBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.GitHub;
        var token = ResolveStoredToken();
        if (config == null || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(remoteFileId))
        {
            return null;
        }

        if (!TryParseRepository(config.Repository, out var owner, out var repo))
        {
            return null;
        }

        try
        {
            var client = CreateGitHubClient(token);
            var zipUrl = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/zipball/{Uri.EscapeDataString(remoteFileId)}";
            var response = await SendWithRateLimitRetryAsync(
                ct => client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct),
                cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[GitHubBackup] Failed to download commit archive {Sha}", remoteFileId);
            return null;
        }
    }

    public Task DeleteBackupAsync(string remoteFileId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task PruneBackupsAsync(int retentionCount, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task<TestConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.GitHub;
        var token = ResolveStoredToken();
        if (config == null || string.IsNullOrWhiteSpace(token))
        {
            return TestConnectionResult.Fail("GitHub Personal Access Token is not configured.");
        }

        if (!TryParseRepository(config.Repository, out var owner, out var repo))
        {
            return TestConnectionResult.Fail("Please select or enter a repository in 'owner/repo' format.");
        }

        try
        {
            var verify = await VerifyTokenAsync(token, cancellationToken);
            if (!verify.Success)
            {
                return TestConnectionResult.Fail(verify.ErrorMessage ?? "GitHub token verification failed.");
            }

            var client = CreateGitHubClient(token);
            var (defaultBranch, hasPush, repoError) = await GetRepositoryMetadataAsync(client, owner, repo, cancellationToken);
            if (defaultBranch == null || repoError != null)
            {
                return TestConnectionResult.Fail(repoError ?? $"Could not access repository '{owner}/{repo}'.");
            }

            if (!hasPush)
            {
                return TestConnectionResult.Fail($"Connected as @{verify.Username}, but token lacks write/push permissions for '{owner}/{repo}'.");
            }

            var folderDisplay = NormalizeTargetFolder(config.TargetFolder);
            var folderLabel = string.IsNullOrEmpty(folderDisplay) ? "/ (repository root)" : $"/{folderDisplay}";
            return TestConnectionResult.Ok($"Successfully connected as @{verify.Username} with write access to '{owner}/{repo}' (branch '{defaultBranch}', folder '{folderLabel}').");
        }
        catch (Exception ex)
        {
            return TestConnectionResult.Fail($"GitHub connection failed: {ex.Message}");
        }
    }
}
