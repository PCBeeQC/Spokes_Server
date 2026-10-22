using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public class GoogleDriveBackupProvider : IBackupDestinationProvider
{
    private readonly CompanyProfileRepository _profiles;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly LicenseValidationService _licenseValidation;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GoogleDriveBackupProvider>? _logger;

    public BackupDestinationType DestinationType => BackupDestinationType.GoogleDrive;

    public GoogleDriveBackupProvider(
        CompanyProfileRepository profiles,
        ServerConfigRepository serverConfigs,
        LicenseValidationService licenseValidation,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GoogleDriveBackupProvider>? logger = null)
    {
        _profiles = profiles;
        _serverConfigs = serverConfigs;
        _licenseValidation = licenseValidation;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string GetRelayUrl()
    {
        var baseRelay = _configuration["Spokes:PushRelayUrl"] ?? SpokesConstants.PushRelayUrl;
        // e.g. "https://push.spokes.sh/api/relay-push" -> "https://push.spokes.sh"
        if (Uri.TryCreate(baseRelay, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Authority}";
        }
        return "https://push.spokes.sh";
    }

    private string GetRelayKey() =>
        _configuration["SPOKES_RELAY_KEY"] ?? _configuration["Spokes:PushRelayKey"] ?? string.Empty;

    public async Task<string?> GetRelayAuthUrlAsync(string callbackUrl, CancellationToken cancellationToken = default)
    {
        var profile = _profiles.Get();
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();

        var licenseStatus = _licenseValidation.ValidateLicense(profile?.LicensePayload, serverConfig);
        if (!LicenseValidationService.IsLicenseActive(licenseStatus.Status))
        {
            _logger?.LogWarning("[GoogleDriveBackup] Setting up a new Google Drive account is blocked: Spokes Pass is required (Status: {Status}).", licenseStatus.Status);
            return null;
        }

        var relayUrl = $"{GetRelayUrl()}/api/relay-backup/google-auth-url?callback={Uri.EscapeDataString(callbackUrl)}";
        var client = _httpClientFactory.CreateClient();
        using var res = await client.GetAsync(relayUrl, cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            _logger?.LogWarning("[GoogleDriveBackup] Failed to fetch Google auth URL from relay: {Status}", res.StatusCode);
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (doc.RootElement.TryGetProperty("authUrl", out var prop))
        {
            return prop.GetString();
        }
        return null;
    }

    public async Task<string?> GetFreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var profile = _profiles.Get();
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();

        var config = profile?.Backup.GoogleDrive;
        if (config == null || string.IsNullOrWhiteSpace(config.RefreshToken))
        {
            _logger?.LogWarning("[GoogleDriveBackup] No refresh token configured.");
            return null;
        }

        var relayUrl = $"{GetRelayUrl()}/api/relay-backup/google-token";
        var client = _httpClientFactory.CreateClient();

        var ticket = RelayTrialTicketService.GetTicketForCurrentKey(serverConfig);
        var fingerprint = RelayTrialTicketService.ComputeFingerprint(serverConfig.ServerMasterRsaPublicKey) ?? string.Empty;

        var requestBody = new
        {
            ServerId = serverConfig.DatabaseCreationId,
            ServerFingerprint = fingerprint,
            LicensePayload = profile?.LicensePayload,
            TrialTicket = ticket,
            RefreshToken = config.RefreshToken
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, relayUrl)
        {
            Content = JsonContent.Create(requestBody)
        };

        var relayKey = GetRelayKey();
        if (!string.IsNullOrEmpty(relayKey))
        {
            request.Headers.Add("X-Relay-Key", relayKey);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger?.LogError("[GoogleDriveBackup] Relay failed to refresh Google token (HTTP {Status}): {Error}", response.StatusCode, err);
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (doc.RootElement.TryGetProperty("accessToken", out var tokenProp))
        {
            return tokenProp.GetString();
        }

        return null;
    }

    private async Task<string> GetOrCreateFolderIdAsync(HttpClient client, string accessToken, string folderName, CancellationToken cancellationToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Query for existing folder
        var query = Uri.EscapeDataString($"name = '{folderName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false");
        var listUrl = $"https://www.googleapis.com/drive/v3/files?q={query}&fields=files(id,name)";

        using var listRes = await client.GetAsync(listUrl, cancellationToken);
        if (listRes.IsSuccessStatusCode)
        {
            using var doc = await JsonDocument.ParseAsync(await listRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
            {
                return files[0].GetProperty("id").GetString()!;
            }
        }

        // Create folder if not found
        var folderMetadata = new
        {
            name = folderName,
            mimeType = "application/vnd.google-apps.folder"
        };

        using var createRes = await client.PostAsJsonAsync("https://www.googleapis.com/drive/v3/files?fields=id", folderMetadata, cancellationToken);
        createRes.EnsureSuccessStatusCode();
        using var createDoc = await JsonDocument.ParseAsync(await createRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return createDoc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task<BackupUploadResult> UploadAsync(
        string localBackupPath,
        string remoteFileName,
        IProgress<BackupUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.GoogleDrive;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.RefreshToken))
        {
            return BackupUploadResult.Failed("Google Drive backup is not enabled or not authenticated.");
        }

        var accessToken = await GetFreshAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(accessToken))
        {
            return BackupUploadResult.Failed("Could not obtain Google Drive access token. Check Relay connectivity and Google Drive settings.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var folderId = await GetOrCreateFolderIdAsync(client, accessToken, config.TargetFolderName, cancellationToken);

            var fileInfo = new FileInfo(localBackupPath);
            var totalBytes = fileInfo.Length;
            progress?.Report(new BackupUploadProgress(0, totalBytes, 0));

            // 1. Initiate Resumable Upload
            var initUrl = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id,name,size";
            var metadata = new
            {
                name = remoteFileName,
                parents = new[] { folderId }
            };

            using var initRequest = new HttpRequestMessage(HttpMethod.Post, initUrl)
            {
                Content = JsonContent.Create(metadata)
            };
            initRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            initRequest.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            initRequest.Headers.Add("X-Upload-Content-Length", totalBytes.ToString());

            using var initResponse = await client.SendAsync(initRequest, cancellationToken);
            if (!initResponse.IsSuccessStatusCode)
            {
                var err = await initResponse.Content.ReadAsStringAsync(cancellationToken);
                return BackupUploadResult.Failed($"Failed to initiate Google Drive resumable upload: {err}");
            }

            var sessionUri = initResponse.Headers.Location;
            if (sessionUri == null)
            {
                return BackupUploadResult.Failed("Google Drive did not return a resumable session URI.");
            }

            // 2. Upload file chunks (8 MB chunks, multiple of 256KB)
            const int chunkSize = 8 * 1024 * 1024;
            var buffer = new byte[chunkSize];
            long bytesSent = 0;

            await using var fileStream = new FileStream(localBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            while (bytesSent < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var toRead = (int)Math.Min(chunkSize, totalBytes - bytesSent);
                var read = await fileStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (read == 0) break;

                var start = bytesSent;
                var end = bytesSent + read - 1;

                using var chunkRequest = new HttpRequestMessage(HttpMethod.Put, sessionUri);
                chunkRequest.Content = new ByteArrayContent(buffer, 0, read);
                chunkRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                chunkRequest.Content.Headers.Add("Content-Range", $"bytes {start}-{end}/{totalBytes}");

                using var chunkResponse = await client.SendAsync(chunkRequest, cancellationToken);
                if (chunkResponse.StatusCode == System.Net.HttpStatusCode.Created || chunkResponse.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    // Upload completed!
                    using var completeDoc = await JsonDocument.ParseAsync(await chunkResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                    var fileId = completeDoc.RootElement.GetProperty("id").GetString()!;
                    progress?.Report(new BackupUploadProgress(totalBytes, totalBytes, 100.0));
                    _logger?.LogInformation("[GoogleDriveBackup] Successfully uploaded {File} (ID: {Id}) to Google Drive folder {Folder}", remoteFileName, fileId, folderId);
                    return BackupUploadResult.Succeeded(fileId, totalBytes);
                }

                if ((int)chunkResponse.StatusCode != 308) // 308 Resume Incomplete
                {
                    var chunkErr = await chunkResponse.Content.ReadAsStringAsync(cancellationToken);
                    return BackupUploadResult.Failed($"Chunk upload failed (HTTP {chunkResponse.StatusCode}): {chunkErr}");
                }

                bytesSent += read;
                double pct = totalBytes > 0 ? (double)bytesSent / totalBytes * 100.0 : 0.0;
                progress?.Report(new BackupUploadProgress(bytesSent, totalBytes, Math.Min(100.0, pct)));
            }

            return BackupUploadResult.Failed("Google Drive upload completed unexpectedly without final confirmation.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[GoogleDriveBackup] Upload failed");
            return BackupUploadResult.Failed(ex.Message);
        }
    }

    public async Task<List<RemoteBackupItem>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.GoogleDrive;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.RefreshToken))
        {
            return new List<RemoteBackupItem>();
        }

        var accessToken = await GetFreshAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(accessToken)) return new List<RemoteBackupItem>();

        try
        {
            var client = _httpClientFactory.CreateClient();
            var folderId = await GetOrCreateFolderIdAsync(client, accessToken, config.TargetFolderName, cancellationToken);

            var query = Uri.EscapeDataString($"'{folderId}' in parents and trashed = false");
            var listUrl = $"https://www.googleapis.com/drive/v3/files?q={query}&fields=files(id,name,size,createdTime)&orderBy=createdTime desc";

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.GetAsync(listUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return new List<RemoteBackupItem>();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var items = new List<RemoteBackupItem>();

            if (doc.RootElement.TryGetProperty("files", out var files))
            {
                foreach (var f in files.EnumerateArray())
                {
                    var id = f.GetProperty("id").GetString()!;
                    var name = f.GetProperty("name").GetString()!;
                    long size = 0;
                    if (f.TryGetProperty("size", out var sProp))
                    {
                        long.TryParse(sProp.GetString(), out size);
                    }

                    DateTime created = DateTime.UtcNow;
                    if (f.TryGetProperty("createdTime", out var cProp))
                    {
                        DateTime.TryParse(cProp.GetString(), out created);
                    }

                    if (name.StartsWith("Spokes_Backup_", StringComparison.OrdinalIgnoreCase))
                    {
                        items.Add(new RemoteBackupItem(id, name, size, created.ToUniversalTime(), "GoogleDrive"));
                    }
                }
            }

            return items;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GoogleDriveBackup] Failed to list backups");
            return new List<RemoteBackupItem>();
        }
    }

    public async Task<Stream?> DownloadBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        var accessToken = await GetFreshAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(accessToken)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var downloadUrl = $"https://www.googleapis.com/drive/v3/files/{remoteFileId}?alt=media";

            var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[GoogleDriveBackup] Failed to download {Id}", remoteFileId);
            return null;
        }
    }

    public async Task DeleteBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        var accessToken = await GetFreshAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(accessToken)) return;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            await client.DeleteAsync($"https://www.googleapis.com/drive/v3/files/{remoteFileId}", cancellationToken);
            _logger?.LogInformation("[GoogleDriveBackup] Deleted backup {Id}", remoteFileId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GoogleDriveBackup] Failed to delete {Id}", remoteFileId);
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
            _logger?.LogError(ex, "[GoogleDriveBackup] Pruning failed");
        }
    }

    public async Task<TestConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.GoogleDrive;
        if (config == null || string.IsNullOrWhiteSpace(config.RefreshToken))
        {
            return TestConnectionResult.Fail("Google Drive account is not connected.");
        }

        var accessToken = await GetFreshAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(accessToken))
        {
            return TestConnectionResult.Fail("Could not verify Google Drive token. Check Relay connectivity and Google Drive settings.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var folderId = await GetOrCreateFolderIdAsync(client, accessToken, config.TargetFolderName, cancellationToken);
            return TestConnectionResult.Ok($"Successfully connected to Google Drive folder '{config.TargetFolderName}' (ID: {folderId}).");
        }
        catch (Exception ex)
        {
            return TestConnectionResult.Fail($"Google Drive connection failed: {ex.Message}");
        }
    }
}
