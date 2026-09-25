using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Services.Core;

public enum StorageStatus
{
    Normal,
    Warning,
    CriticalReadOnly
}

public class StorageHealthInfo
{
    public StorageStatus Status { get; set; } = StorageStatus.Normal;
    public long AvailableFreeBytes { get; set; }
    public long TotalBytes { get; set; }
    public int AvailableFreeMegabytes => (int)(AvailableFreeBytes / (1024 * 1024));
    public bool IsUploadAllowed => Status != StorageStatus.CriticalReadOnly;
    public bool HasPersistenceFailure { get; set; }
}

public interface IStorageHealthService
{
    StorageStatus Status { get; }
    long AvailableFreeBytes { get; }
    long TotalBytes { get; }
    int AvailableFreeMegabytes { get; }
    bool IsUploadAllowed { get; }
    bool HasPersistenceFailure { get; }
    StorageHealthInfo GetHealthInfo();
    void RecordPersistenceFailure();
    void RecordPersistenceSuccess();
    void ForceRefresh();
}

public class StorageHealthService : IStorageHealthService
{
    private readonly string _dataPath;
    private readonly ILogger<StorageHealthService> _logger;
    private readonly object _lock = new();

    private DateTime _lastCheckedUtc = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    private StorageStatus _status = StorageStatus.Normal;
    private long _availableFreeBytes = long.MaxValue;
    private long _totalBytes = long.MaxValue;
    private bool _hasPersistenceFailure = false;

    // Thresholds: Warning at 1 GB, Critical Read-Only at 100 MB
    public const long WarningThresholdBytes = 1024L * 1024L * 1024L; // 1 GB
    public const long CriticalThresholdBytes = 100L * 1024L * 1024L;   // 100 MB

    public StorageHealthService(IConfiguration config, ILogger<StorageHealthService> logger)
    {
        _logger = logger;
        _dataPath = config["DataPath"] ?? "Data";
        RefreshIfStale();
    }

    public StorageStatus Status
    {
        get
        {
            RefreshIfStale();
            return _status;
        }
    }

    public long AvailableFreeBytes
    {
        get
        {
            RefreshIfStale();
            return _availableFreeBytes;
        }
    }

    public long TotalBytes
    {
        get
        {
            RefreshIfStale();
            return _totalBytes;
        }
    }

    public int AvailableFreeMegabytes => (int)(AvailableFreeBytes / (1024 * 1024));

    public bool IsUploadAllowed => Status != StorageStatus.CriticalReadOnly;

    public bool HasPersistenceFailure
    {
        get
        {
            RefreshIfStale();
            return _hasPersistenceFailure;
        }
    }

    public StorageHealthInfo GetHealthInfo()
    {
        RefreshIfStale();
        return new StorageHealthInfo
        {
            Status = _status,
            AvailableFreeBytes = _availableFreeBytes,
            TotalBytes = _totalBytes,
            HasPersistenceFailure = _hasPersistenceFailure
        };
    }

    public void RecordPersistenceFailure()
    {
        lock (_lock)
        {
            _hasPersistenceFailure = true;
            _status = StorageStatus.CriticalReadOnly;
            _lastCheckedUtc = DateTime.UtcNow;
            _logger.LogCritical("[StorageHealth] Persistence failure recorded. System entered CriticalReadOnly mode.");
        }
    }

    public void RecordPersistenceSuccess()
    {
        lock (_lock)
        {
            if (_hasPersistenceFailure)
            {
                _hasPersistenceFailure = false;
                _lastCheckedUtc = DateTime.MinValue; // Trigger immediate recheck
                _logger.LogInformation("[StorageHealth] Persistence recovered.");
            }
        }
    }

    public void ForceRefresh()
    {
        lock (_lock)
        {
            _lastCheckedUtc = DateTime.MinValue;
            RefreshIfStale();
        }
    }

    private void RefreshIfStale()
    {
        if (DateTime.UtcNow - _lastCheckedUtc < _cacheDuration) return;

        lock (_lock)
        {
            if (DateTime.UtcNow - _lastCheckedUtc < _cacheDuration) return;

            try
            {
                var fullDataPath = Path.GetFullPath(_dataPath);
                var root = Path.GetPathRoot(fullDataPath);

                if (!string.IsNullOrEmpty(root))
                {
                    var driveInfo = new DriveInfo(root);
                    if (driveInfo.IsReady)
                    {
                        _availableFreeBytes = driveInfo.AvailableFreeSpace;
                        _totalBytes = driveInfo.TotalSize;

                        if (_hasPersistenceFailure || _availableFreeBytes < CriticalThresholdBytes)
                        {
                            _status = StorageStatus.CriticalReadOnly;
                        }
                        else if (_availableFreeBytes < WarningThresholdBytes)
                        {
                            _status = StorageStatus.Warning;
                        }
                        else
                        {
                            _status = StorageStatus.Normal;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StorageHealth] Unable to query DriveInfo for {Path}", _dataPath);
            }
            finally
            {
                _lastCheckedUtc = DateTime.UtcNow;
            }
        }
    }
}
