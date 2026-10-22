using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Maintenance.Destinations;

public class S3BackupProvider : IBackupDestinationProvider
{
    private readonly CompanyProfileRepository _profiles;
    private readonly ILogger<S3BackupProvider>? _logger;

    public BackupDestinationType DestinationType => BackupDestinationType.S3;

    public S3BackupProvider(CompanyProfileRepository profiles, ILogger<S3BackupProvider>? logger = null)
    {
        _profiles = profiles;
        _logger = logger;
    }

    private AmazonS3Client CreateClient()
    {
        var config = _profiles.Get()?.Backup.S3 ?? throw new InvalidOperationException("S3 is not configured.");
        var s3Config = new AmazonS3Config
        {
            ForcePathStyle = config.ForcePathStyle
        };

        if (!string.IsNullOrWhiteSpace(config.Endpoint))
        {
            s3Config.ServiceURL = config.Endpoint;
            s3Config.AuthenticationRegion = string.IsNullOrWhiteSpace(config.Region) ? "auto" : config.Region;
        }
        else if (!string.IsNullOrWhiteSpace(config.Region) && config.Region != "auto")
        {
            s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(config.Region);
        }

        return new AmazonS3Client(config.AccessKey, config.SecretKey, s3Config);
    }

    internal static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return "";
        var trimmed = prefix.Trim().Trim('/');
        return string.IsNullOrEmpty(trimmed) ? "" : trimmed + "/";
    }

    public async Task<BackupUploadResult> UploadAsync(
        string localBackupPath,
        string remoteFileName,
        IProgress<BackupUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.S3;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.BucketName))
        {
            return BackupUploadResult.Failed("S3 backup destination is not configured or enabled.");
        }

        try
        {
            using var client = CreateClient();
            using var transferUtility = new TransferUtility(client);

            var prefix = NormalizePrefix(config.Prefix);
            var s3Key = $"{prefix}{remoteFileName}";

            var fileInfo = new FileInfo(localBackupPath);
            progress?.Report(new BackupUploadProgress(0, fileInfo.Length, 0));

            var uploadRequest = new TransferUtilityUploadRequest
            {
                BucketName = config.BucketName,
                FilePath = localBackupPath,
                Key = s3Key
            };

            uploadRequest.UploadProgressEvent += (sender, args) =>
            {
                progress?.Report(new BackupUploadProgress(args.TransferredBytes, args.TotalBytes, args.PercentDone));
            };

            await transferUtility.UploadAsync(uploadRequest, cancellationToken);
            progress?.Report(new BackupUploadProgress(fileInfo.Length, fileInfo.Length, 100.0));
            _logger?.LogInformation("[S3Backup] Successfully uploaded {File} to S3 bucket {Bucket} at key {Key}", remoteFileName, config.BucketName, s3Key);

            return BackupUploadResult.Succeeded(s3Key, fileInfo.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[S3Backup] Failed to upload backup to S3 bucket {Bucket}", config.BucketName);
            return BackupUploadResult.Failed(ex.Message);
        }
    }

    public async Task<List<RemoteBackupItem>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.S3;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.BucketName))
        {
            return new List<RemoteBackupItem>();
        }

        try
        {
            using var client = CreateClient();
            var prefix = NormalizePrefix(config.Prefix);

            var request = new ListObjectsV2Request
            {
                BucketName = config.BucketName,
                Prefix = prefix
            };

            var items = new List<RemoteBackupItem>();
            ListObjectsV2Response response;
            do
            {
                response = await client.ListObjectsV2Async(request, cancellationToken);
                foreach (var obj in response.S3Objects)
                {
                    var fileName = Path.GetFileName(obj.Key);
                    if (fileName.StartsWith("Spokes_Backup_", StringComparison.OrdinalIgnoreCase) &&
                        (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".spkbackup", StringComparison.OrdinalIgnoreCase)))
                    {
                        items.Add(new RemoteBackupItem(obj.Key, fileName, obj.Size ?? 0L, (obj.LastModified ?? DateTime.UtcNow).ToUniversalTime(), "S3"));
                    }
                }
                request.ContinuationToken = response.NextContinuationToken;
            } while (response.IsTruncated == true);

            return items.OrderByDescending(i => i.CreatedAtUtc).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[S3Backup] Failed to list backups from S3");
            return new List<RemoteBackupItem>();
        }
    }

    public async Task<Stream?> DownloadBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.S3;
        if (config == null || string.IsNullOrWhiteSpace(config.BucketName)) return null;

        try
        {
            var client = CreateClient();
            var response = await client.GetObjectAsync(config.BucketName, remoteFileId, cancellationToken);
            return response.ResponseStream;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[S3Backup] Failed to download {Key} from S3", remoteFileId);
            return null;
        }
    }

    public async Task DeleteBackupAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.S3;
        if (config == null || string.IsNullOrWhiteSpace(config.BucketName)) return;

        try
        {
            using var client = CreateClient();
            await client.DeleteObjectAsync(config.BucketName, remoteFileId, cancellationToken);
            _logger?.LogInformation("[S3Backup] Deleted backup {Key} from S3 bucket {Bucket}", remoteFileId, config.BucketName);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[S3Backup] Failed to delete {Key} from S3", remoteFileId);
        }
    }

    public async Task PruneBackupsAsync(int retentionCount, CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.S3;
        if (config == null || !config.IsEnabled || string.IsNullOrWhiteSpace(config.BucketName)) return;

        try
        {
            var backups = await ListBackupsAsync(cancellationToken);
            var scheduled = backups.Where(b => !b.FileName.Contains("_Manual_", StringComparison.OrdinalIgnoreCase)).ToList();

            if (scheduled.Count > retentionCount)
            {
                using var client = CreateClient();
                foreach (var old in scheduled.Skip(retentionCount))
                {
                    try
                    {
                        await client.DeleteObjectAsync(config.BucketName, old.Id, cancellationToken);
                        _logger?.LogInformation("[S3Backup] Pruned old backup from S3: {Key}", old.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[S3Backup] Failed to prune {Key}", old.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[S3Backup] Error during S3 pruning");
        }
    }

    public async Task<TestConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = _profiles.Get()?.Backup.S3;
        if (config == null || string.IsNullOrWhiteSpace(config.BucketName))
        {
            return TestConnectionResult.Fail("Bucket Name cannot be empty.");
        }

        try
        {
            using var client = CreateClient();

            // 1. Verify read / list access
            var prefix = NormalizePrefix(config.Prefix);
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = config.BucketName,
                Prefix = prefix,
                MaxKeys = 1
            }, cancellationToken);

            // 2. Verify write and delete permissions with a small probe object
            var probeKey = $"{prefix}.spokes_probe_{Guid.NewGuid():N}";
            using (var ms = new MemoryStream(new byte[] { 1 }))
            {
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = config.BucketName,
                    Key = probeKey,
                    InputStream = ms
                }, cancellationToken);
            }

            try
            {
                await client.DeleteObjectAsync(config.BucketName, probeKey, cancellationToken);
            }
            catch (Exception ex)
            {
                return TestConnectionResult.Fail($"S3 connection succeeded and test file was written, but delete permission failed: {ex.Message}");
            }

            return TestConnectionResult.Ok($"Successfully connected to S3 bucket '{config.BucketName}' and verified read/write/delete permissions.");
        }
        catch (Exception ex)
        {
            return TestConnectionResult.Fail($"S3 connection failed: {ex.Message}");
        }
    }
}
