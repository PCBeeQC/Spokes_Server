using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public class WebDavBackupProvider : IBackupDestinationProvider
{
    private readonly CompanyProfileRepository _profiles;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebDavBackupProvider>? _logger;

    public BackupDestinationType DestinationType => BackupDestinationType.WebDav;

    public WebDavBackupProvider(
        CompanyProfileRepository profiles,
        IHttpClientFactory httpClientFactory,
        ILogger<WebDavBackupProvider>? logger = null)
    {
        _profiles = profiles;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private (HttpClient client, string baseUrl) CreateClient()
    {
        var config = _profiles.Get()?.Backup.WebDav ?? throw new InvalidOperationException("WebDAV is not configured.");
        var client = _httpClientFactory.CreateClient("WebDavBackup");
        var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        return (client, config.ServerUrl.TrimEnd('/') + "/");
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
            var (client, baseUrl) = CreateClient();
            var targetUrl = baseUrl + remoteFileName;

            var fileInfo = new FileInfo(localBackupPath);
            progress?.Report(new BackupUploadProgress(0, fileInfo.Length, 0));

            await using var fileStream = new FileStream(localBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var reportingStream = new ProgressReportingStream(fileStream, fileInfo.Length, progress);
            using var content = new StreamContent(reportingStream, 81920);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await client.PutAsync(targetUrl, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogError("[WebDavBackup] Upload failed with HTTP {Status}: {Error}", response.StatusCode, err);
                return BackupUploadResult.Failed($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            progress?.Report(new BackupUploadProgress(fileInfo.Length, fileInfo.Length, 100.0));
            _logger?.LogInformation("[WebDavBackup] Successfully uploaded {File} to WebDAV target {Url}", remoteFileName, targetUrl);
            return BackupUploadResult.Succeeded(targetUrl, fileInfo.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WebDavBackup] Upload failed to {Url}", config.ServerUrl);
            return BackupUploadResult.Failed(ex.Message);
        }
    }

    private sealed class ProgressReportingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _totalBytes;
        private readonly IProgress<BackupUploadProgress>? _progress;
        private long _bytesRead;

        public ProgressReportingStream(Stream inner, long totalBytes, IProgress<BackupUploadProgress>? progress)
        {
            _inner = inner;
            _totalBytes = totalBytes;
            _progress = progress;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (read > 0)
            {
                _bytesRead += read;
                double pct = _totalBytes > 0 ? (double)_bytesRead / _totalBytes * 100.0 : 0.0;
                _progress?.Report(new BackupUploadProgress(_bytesRead, _totalBytes, Math.Min(100.0, pct)));
            }
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                _bytesRead += read;
                double pct = _totalBytes > 0 ? (double)_bytesRead / _totalBytes * 100.0 : 0.0;
                _progress?.Report(new BackupUploadProgress(_bytesRead, _totalBytes, Math.Min(100.0, pct)));
            }
            return read;
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
            var (client, baseUrl) = CreateClient();
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), baseUrl);
            req.Headers.Add("Depth", "1");
            req.Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"utf-8\" ?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:getcontentlength/><d:getlastmodified/><d:resourcetype/></d:prop></d:propfind>",
                Encoding.UTF8,
                "application/xml");

            using var res = await client.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode) return new List<RemoteBackupItem>();

            var xml = await res.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(xml);
            XNamespace d = "DAV:";

            var items = new List<RemoteBackupItem>();
            foreach (var responseElem in doc.Descendants(d + "response"))
            {
                var href = responseElem.Element(d + "href")?.Value;
                if (string.IsNullOrEmpty(href)) continue;

                var fileName = Path.GetFileName(Uri.UnescapeDataString(href.TrimEnd('/')));
                if (!fileName.StartsWith("Spokes_Backup_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".spkbackup", StringComparison.OrdinalIgnoreCase)) continue;

                var prop = responseElem.Descendants(d + "prop").FirstOrDefault();
                long size = 0;
                DateTime modified = DateTime.UtcNow;

                if (prop != null)
                {
                    long.TryParse(prop.Element(d + "getcontentlength")?.Value, out size);
                    DateTime.TryParse(prop.Element(d + "getlastmodified")?.Value, out modified);
                }

                var fullUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : new Uri(new Uri(baseUrl), href).ToString();
                items.Add(new RemoteBackupItem(fullUrl, fileName, size, modified.ToUniversalTime(), "WebDAV"));
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
            var (client, _) = CreateClient();
            var response = await client.GetAsync(remoteFileId, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
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
            var (client, _) = CreateClient();
            using var response = await client.DeleteAsync(remoteFileId, cancellationToken);
            _logger?.LogInformation("[WebDavBackup] Deleted {Url} (Status: {Status})", remoteFileId, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[WebDavBackup] Failed to delete {Url}", remoteFileId);
        }
    }

    public async Task PruneBackupsAsync(int retentionCount, CancellationToken cancellationToken = default)
    {
        try
        {
            var backups = await ListBackupsAsync(cancellationToken);
            var scheduled = backups.Where(b => !b.FileName.Contains("_Manual_", StringComparison.OrdinalIgnoreCase)).ToList();

            if (scheduled.Count > retentionCount)
            {
                foreach (var old in scheduled.Skip(retentionCount))
                {
                    await DeleteBackupAsync(old.Id, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WebDavBackup] Pruning failed");
        }
    }

    public async Task<TestConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.WebDav;
        if (config == null || string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            return TestConnectionResult.Fail("Server URL cannot be empty.");
        }

        try
        {
            var (client, baseUrl) = CreateClient();

            // 1. Verify endpoint accessibility
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), baseUrl);
            req.Headers.Add("Depth", "0");
            using var res = await client.SendAsync(req, cancellationToken);

            if (!res.IsSuccessStatusCode)
            {
                return TestConnectionResult.Fail($"WebDAV returned HTTP {(int)res.StatusCode} {res.ReasonPhrase}.");
            }

            // 2. Verify write and delete permissions with a small probe file
            var probeUrl = baseUrl + $".spokes_probe_{Guid.NewGuid():N}";
            using var content = new ByteArrayContent(new byte[] { 1 });
            using var putRes = await client.PutAsync(probeUrl, content, cancellationToken);
            if (!putRes.IsSuccessStatusCode)
            {
                return TestConnectionResult.Fail($"WebDAV connected, but write permission test failed: HTTP {(int)putRes.StatusCode} {putRes.ReasonPhrase}");
            }

            try
            {
                using var delRes = await client.DeleteAsync(probeUrl, cancellationToken);
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
