using System;
using Spokes_Server.Core.Services.Maintenance.Destinations;

namespace Spokes_Server.Core.Configuration;

public interface IBackupDestinationConfig
{
    bool IsEnabled { get; set; }
}

public class BackupConfig
{
    public bool BackupsEnabled { get; set; }
    public int BackupRetentionCount { get; set; } = 5;
    public string BackupTimeLocal { get; set; } = "03:00"; // HH:mm Local
    public bool BackupEmployeeEmails { get; set; }

    /// <summary>
    /// When enabled, backup archives are encrypted with the server's master password (SPOKES_MASTER_PASSWORD)
    /// producing a .spkbackup envelope. When disabled, backups remain standard readable .zip archives.
    /// </summary>
    public bool EncryptBackupsWithMasterPassword { get; set; }

    // Multi-Destination Configurations
    private LocalMountDestinationConfig _secondaryMount = new();
    public LocalMountDestinationConfig SecondaryMount { get => _secondaryMount ??= new(); set => _secondaryMount = value ?? new(); }

    private S3DestinationConfig _s3 = new();
    public S3DestinationConfig S3 { get => _s3 ??= new(); set => _s3 = value ?? new(); }

    private GoogleDriveDestinationConfig _googleDrive = new();
    public GoogleDriveDestinationConfig GoogleDrive { get => _googleDrive ??= new(); set => _googleDrive = value ?? new(); }

    private GitHubDestinationConfig _gitHub = new();
    public GitHubDestinationConfig GitHub { get => _gitHub ??= new(); set => _gitHub = value ?? new(); }

    private WebDavDestinationConfig _webDav = new();
    public WebDavDestinationConfig WebDav { get => _webDav ??= new(); set => _webDav = value ?? new(); }

    private SftpDestinationConfig _sftp = new();
    public SftpDestinationConfig Sftp { get => _sftp ??= new(); set => _sftp = value ?? new(); }

    public IBackupDestinationConfig GetDestinationConfig(BackupDestinationType type) => type switch
    {
        BackupDestinationType.LocalMount => SecondaryMount,
        BackupDestinationType.S3 => S3,
        BackupDestinationType.GoogleDrive => GoogleDrive,
        BackupDestinationType.GitHub => GitHub,
        BackupDestinationType.WebDav => WebDav,
        BackupDestinationType.Sftp => Sftp,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}

public class LocalMountDestinationConfig : IBackupDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string MountPath { get; set; } = string.Empty;
    public int RetentionCount { get; set; } = 5;
}

public class S3DestinationConfig : IBackupDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string Endpoint { get; set; } = string.Empty; // e.g. https://<accountid>.r2.cloudflarestorage.com or http://minio:9000
    public string BucketName { get; set; } = string.Empty;
    public string Region { get; set; } = "auto";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Prefix { get; set; } = "spokes-backups";
    public bool ForcePathStyle { get; set; } = true;
    public int RetentionCount { get; set; } = 5;
}

public class GoogleDriveDestinationConfig : IBackupDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public string TargetFolderName { get; set; } = "Spokes Backups";
    public int RetentionCount { get; set; } = 5;
}

public class GitHubDestinationConfig : IBackupDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string PersonalAccessToken { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty; // e.g. "owner/repo"
    public string TargetFolder { get; set; } = string.Empty;
    public string? AccountUsername { get; set; }
}

public class WebDavDestinationConfig : IBackupDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string ServerUrl { get; set; } = string.Empty; // e.g. https://nextcloud.example.com/remote.php/dav/files/user/backups/
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int RetentionCount { get; set; } = 5;
}

public class SftpDestinationConfig : IBackupDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string RemotePath { get; set; } = "/backups";
    public int RetentionCount { get; set; } = 5;
    public string TrustedHostKeyFingerprint { get; set; } = string.Empty;
}
