namespace Spokes_Server.Core.Configuration;

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

    private WebDavDestinationConfig _webDav = new();
    public WebDavDestinationConfig WebDav { get => _webDav ??= new(); set => _webDav = value ?? new(); }

    private SftpDestinationConfig _sftp = new();
    public SftpDestinationConfig Sftp { get => _sftp ??= new(); set => _sftp = value ?? new(); }
}

public class LocalMountDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string MountPath { get; set; } = string.Empty;
    public int RetentionCount { get; set; } = 5;
}

public class S3DestinationConfig
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

public class GoogleDriveDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public string TargetFolderName { get; set; } = "Spokes Backups";
    public string? AccountEmail { get; set; }
    public int RetentionCount { get; set; } = 5;
}

public class WebDavDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string ServerUrl { get; set; } = string.Empty; // e.g. https://nextcloud.example.com/remote.php/dav/files/user/backups/
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int RetentionCount { get; set; } = 5;
}

public class SftpDestinationConfig
{
    public bool IsEnabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string RemotePath { get; set; } = "/backups";
    public int RetentionCount { get; set; } = 5;
}
