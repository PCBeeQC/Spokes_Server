using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public class WebDavBackupProvider : IBackupDestinationProvider
{
    private readonly CompanyProfileRepository _profiles;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EncryptionService _encryptionService;
    private readonly ILogger<WebDavBackupProvider>? _logger;

    public BackupDestinationType DestinationType => BackupDestinationType.WebDav;

    public WebDavBackupProvider(
        CompanyProfileRepository profiles,
        IHttpClientFactory httpClientFactory,
        ILogger<WebDavBackupProvider>? logger = null,
        EncryptionService? encryptionService = null,
        IConfiguration? configuration = null)
    {
        _profiles = profiles;
        _httpClientFactory = httpClientFactory;
        _encryptionService = BackupDestinationPruneHelper.ResolveEncryptionService(encryptionService, profiles, configuration);
        _logger = logger;
    }

    internal static bool TryParseConfiguredBaseUri(string? serverUrl, out Uri baseUri)
    {
        baseUri = null!;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return false;
        }

        var normalized = serverUrl.Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) || !parsed.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        baseUri = parsed;
        return true;
    }

    internal static bool TryResolveSameOriginUri(Uri baseUri, string? hrefOrUrl, out Uri resolvedUri)
    {
        resolvedUri = null!;
        if (baseUri == null || !baseUri.IsAbsoluteUri || string.IsNullOrWhiteSpace(hrefOrUrl))
        {
            return false;
        }

        var trimmed = hrefOrUrl.Trim();
        if (trimmed.Contains('\\'))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, trimmed, out var candidate) || !candidate.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.IdnHost, baseUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != baseUri.Port ||
            !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        resolvedUri = candidate;
        return true;
    }

    private string ResolveCredential(string? value) =>
        BackupDestinationPruneHelper.ResolveCredential(_encryptionService, value);

    private (HttpClient client, Uri baseUri) CreateClient()
    {
        var config = _profiles.Get()?.Backup.WebDav ?? throw new InvalidOperationException("WebDAV is not configured.");
        if (!TryParseConfiguredBaseUri(config.ServerUrl, out var baseUri))
        {
            throw new InvalidOperationException("WebDAV Server URL must be a valid HTTP or HTTPS URL.");
        }

        var client = _httpClientFactory.CreateClient("WebDavBackup");
        var username = ResolveCredential(config.Username);
        var password = ResolveCredential(config.Password);
        var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        return (client, baseUri);
    }

    public async Task<BackupUploadResult> UploadAsync(
        string localBackupPath,
        string remoteFileName,
        IProgress<BackupUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.WebDav;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            return BackupUploadResult.Failed("WebDAV backup destination is not configured or enabled.");
        }

        try
        {
            var safeFileName = Path.GetFileName(remoteFileName);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                return BackupUploadResult.Failed("Remote backup file name cannot be empty.");
            }

            var (client, baseUri) = CreateClient();
            if (!TryResolveSameOriginUri(baseUri, Uri.EscapeDataString(safeFileName), out var targetUri))
            {
                return BackupUploadResult.Failed("Resolved WebDAV upload URL did not match the configured server origin.");
            }

            var fileInfo = new FileInfo(localBackupPath);
            progress?.Report(new BackupUploadProgress(0, fileInfo.Length, 0));

            await using var fileStream = new FileStream(localBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            long totalBytes = fileInfo.Length;
            await using var reportingStream = new ProgressStream(fileStream, totalBytes, bytes =>
            {
                double pct = totalBytes > 0 ? (double)bytes / totalBytes * 100.0 : 0.0;
                progress?.Report(new BackupUploadProgress(bytes, totalBytes, Math.Min(100.0, pct)));
            });
            using var content = new StreamContent(reportingStream, 81920);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await client.PutAsync(targetUri, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogError("[WebDavBackup] Upload failed with HTTP {Status}: {Error}", response.StatusCode, err);
                return BackupUploadResult.Failed($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var targetUrl = targetUri.AbsoluteUri;
            progress?.Report(new BackupUploadProgress(fileInfo.Length, fileInfo.Length, 100.0));
            _logger?.LogInformation("[WebDavBackup] Successfully uploaded {File} to WebDAV target {Url}", safeFileName, targetUrl);
            return BackupUploadResult.Succeeded(targetUrl, fileInfo.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WebDavBackup] Upload failed to {Url}", config.ServerUrl);
            return BackupUploadResult.Failed(ex.Message);
        }
    }


    public async Task<List<RemoteBackupItem>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.WebDav;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            return new List<RemoteBackupItem>();
        }

        try
        {
            var (client, baseUri) = CreateClient();
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), baseUri);
            req.Headers.Add("Depth", "1");
            req.Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"utf-8\" ?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:getcontentlength/><d:getlastmodified/><d:resourcetype/></d:prop></d:propfind>",
                Encoding.UTF8,
                "application/xml");

            using var res = await client.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode) return new List<RemoteBackupItem>();

            var xml = await res.Content.ReadAsStringAsync(cancellationToken);
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var doc = XDocument.Load(xmlReader);
            XNamespace d = "DAV:";

            var items = new List<RemoteBackupItem>();
            foreach (var responseElem in doc.Descendants(d + "response"))
            {
                var href = responseElem.Element(d + "href")?.Value;
                if (string.IsNullOrWhiteSpace(href)) continue;

                if (!TryResolveSameOriginUri(baseUri, href, out var resolvedUri))
                {
                    _logger?.LogWarning(
                        "[WebDavBackup] Skipping cross-origin or invalid WebDAV href '{Href}' (expected origin {Origin}).",
                        href,
                        baseUri.GetLeftPart(UriPartial.Authority));
                    continue;
                }

                var fileName = Path.GetFileName(Uri.UnescapeDataString(resolvedUri.AbsolutePath.TrimEnd('/')));
                if (!fileName.StartsWith("Spokes_Backup_", StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName.EndsWith(".tmp.zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.EndsWith(".spkbackup", StringComparison.OrdinalIgnoreCase)) continue;

                var prop = responseElem.Descendants(d + "prop").FirstOrDefault();
                long size = 0;
                DateTime modified = DateTime.UtcNow;

                if (prop != null)
                {
                    long.TryParse(prop.Element(d + "getcontentlength")?.Value, out size);
                    DateTime.TryParse(prop.Element(d + "getlastmodified")?.Value, out modified);
                }

                items.Add(new RemoteBackupItem(resolvedUri.AbsoluteUri, fileName, size, modified.ToUniversalTime(), "WebDAV"));
            }

            return items.OrderByDescending(i => i.CreatedAtUtc).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[WebDavBackup] Failed to list backups");
            return new List<RemoteBackupItem>();
        }
    }

    public async Task<Stream?> DownloadBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (client, baseUri) = CreateClient();
            if (!TryResolveSameOriginUri(baseUri, remoteFileId, out var targetUri))
            {
                _logger?.LogWarning(
                    "[WebDavBackup] Blocked cross-origin or invalid WebDAV download URL '{Url}' (expected origin {Origin}).",
                    remoteFileId,
                    baseUri.GetLeftPart(UriPartial.Authority));
                return null;
            }

            var response = await client.GetAsync(targetUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return null;
            }

            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WebDavBackup] Failed to download {Url}", remoteFileId);
            return null;
        }
    }

    public async Task DeleteBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (client, baseUri) = CreateClient();
            if (!TryResolveSameOriginUri(baseUri, remoteFileId, out var targetUri))
            {
                _logger?.LogWarning(
                    "[WebDavBackup] Blocked cross-origin or invalid WebDAV delete URL '{Url}' (expected origin {Origin}).",
                    remoteFileId,
                    baseUri.GetLeftPart(UriPartial.Authority));
                return;
            }

            using var response = await client.DeleteAsync(targetUri, cancellationToken);
            _logger?.LogInformation("[WebDavBackup] Deleted {Url} (Status: {Status})", targetUri.AbsoluteUri, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[WebDavBackup] Failed to delete {Url}", remoteFileId);
        }
    }

    public Task PruneBackupsAsync(int retentionCount, CancellationToken cancellationToken = default) =>
        BackupDestinationPruneHelper.PruneScheduledBackupsAsync(this, retentionCount, _logger, "WebDavBackup", cancellationToken);

    public async Task<TestConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.WebDav;
        if (config == null || string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            return TestConnectionResult.Fail("Server URL cannot be empty.");
        }

        try
        {
            var (client, baseUri) = CreateClient();

            // 1. Verify endpoint accessibility
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), baseUri);
            req.Headers.Add("Depth", "0");
            using var res = await client.SendAsync(req, cancellationToken);

            if (!res.IsSuccessStatusCode)
            {
                return TestConnectionResult.Fail($"WebDAV returned HTTP {(int)res.StatusCode} {res.ReasonPhrase}.");
            }

            // 2. Verify write and delete permissions with a small probe file
            if (!TryResolveSameOriginUri(baseUri, $".spokes_probe_{Guid.NewGuid():N}", out var probeUri))
            {
                return TestConnectionResult.Fail("Failed to construct same-origin probe URL.");
            }

            using var content = new ByteArrayContent(new byte[] { 1 });
            using var putRes = await client.PutAsync(probeUri, content, cancellationToken);
            if (!putRes.IsSuccessStatusCode)
            {
                return TestConnectionResult.Fail($"WebDAV connected, but write permission test failed: HTTP {(int)putRes.StatusCode} {putRes.ReasonPhrase}");
            }

            try
            {
                using var delRes = await client.DeleteAsync(probeUri, cancellationToken);
            }
            catch (Exception ex)
            {
                return TestConnectionResult.Fail($"WebDAV connected and wrote test file, but delete permission test failed: {ex.Message}");
            }

            return TestConnectionResult.Ok("Successfully connected and verified write/delete permissions on WebDAV server.");
        }
        catch (Exception ex)
        {
            return TestConnectionResult.Fail($"WebDAV connection failed: {ex.Message}");
        }
    }
}
