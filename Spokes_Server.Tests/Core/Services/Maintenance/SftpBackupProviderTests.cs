using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Maintenance.Destinations;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class SftpBackupProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly DiskPersistenceService _persistence;
    private readonly IConfiguration _config;
    private readonly CompanyProfileRepository _profiles;

    public SftpBackupProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_SftpTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _testDir })
            .Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _profiles = new CompanyProfileRepository(_persistence, _config);

        var profile = _profiles.Get();
        profile.Backup.Sftp.IsEnabled = true;
        profile.Backup.Sftp.Host = "192.168.1.50";
        profile.Backup.Sftp.Port = 2222;
        profile.Backup.Sftp.Username = "backup";
        profile.Backup.Sftp.TrustedHostKeyFingerprint = string.Empty;
        _profiles.Save(profile);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task VerifyOrPinHostKey_OnFirstConnection_TofuPinsCanonicalSha256FingerprintAndPersistsToDisk()
    {
        var hostKey = RandomNumberGenerator.GetBytes(256);
        var expectedFingerprint = $"SHA256:{Convert.ToBase64String(SHA256.HashData(hostKey))}";

        var provider = new SftpBackupProvider(_profiles);
        var trusted = provider.VerifyOrPinHostKey(hostKey, out var errorMessage);
        await _persistence.FlushAllAsync();

        Assert.True(trusted);
        Assert.Null(errorMessage);
        Assert.Equal(expectedFingerprint, _profiles.Get().Backup.Sftp.TrustedHostKeyFingerprint);

        // Verify persisted on disk by loading a fresh CompanyProfileRepository instance
        var reloadedProfiles = new CompanyProfileRepository(_persistence, _config);
        Assert.Equal(expectedFingerprint, reloadedProfiles.Get().Backup.Sftp.TrustedHostKeyFingerprint);
    }

    [Fact]
    public void VerifyOrPinHostKey_SubsequentConnectionWithMatchingKey_AllowsPaddedAndOpenSshUnpaddedSha256Formats()
    {
        var hostKey = RandomNumberGenerator.GetBytes(256);
        var provider = new SftpBackupProvider(_profiles);

        // 1. TOFU pin canonical padded SHA256:<base64>
        Assert.True(provider.VerifyOrPinHostKey(hostKey, out _));
        Assert.True(provider.VerifyOrPinHostKey(hostKey, out var secondErr));
        Assert.Null(secondErr);

        // 2. OpenSSH ssh-keygen -lf style unpadded SHA256:<base64-without-equals> pasted manually by admin
        var unpadded = SftpBackupProvider.ComputeHostKeyFingerprint(hostKey).TrimEnd('=');
        var profile = _profiles.Get();
        profile.Backup.Sftp.TrustedHostKeyFingerprint = unpadded;
        _profiles.Save(profile);

        Assert.True(provider.VerifyOrPinHostKey(hostKey, out var unpaddedErr));
        Assert.Null(unpaddedErr);
    }

    [Fact]
    public void VerifyOrPinHostKey_SubsequentConnectionWithDifferentHostKey_RejectsConnectionWithMitmError()
    {
        var legitimateHostKey = RandomNumberGenerator.GetBytes(256);
        var mitmHostKey = RandomNumberGenerator.GetBytes(256);
        var legitimateFingerprint = SftpBackupProvider.ComputeHostKeyFingerprint(legitimateHostKey);

        var provider = new SftpBackupProvider(_profiles);
        Assert.True(provider.VerifyOrPinHostKey(legitimateHostKey, out _));

        // Attacker / unexpected key change on subsequent connection
        var trusted = provider.VerifyOrPinHostKey(mitmHostKey, out var errorMessage);

        Assert.False(trusted);
        Assert.NotNull(errorMessage);
        Assert.Contains("SSH host key verification failed", errorMessage);
        Assert.Contains("192.168.1.50:2222", errorMessage);
        Assert.Contains("MITM", errorMessage);

        // Pinned fingerprint must NOT be overwritten by the untrusted key
        Assert.Equal(legitimateFingerprint, _profiles.Get().Backup.Sftp.TrustedHostKeyFingerprint);
    }

    [Fact]
    public void VerifyOrPinHostKey_AfterAdminReset_ReTrustsAndPinsNewHostKey()
    {
        var oldHostKey = RandomNumberGenerator.GetBytes(256);
        var newHostKey = RandomNumberGenerator.GetBytes(256);
        var newFingerprint = SftpBackupProvider.ComputeHostKeyFingerprint(newHostKey);

        var provider = new SftpBackupProvider(_profiles);
        Assert.True(provider.VerifyOrPinHostKey(oldHostKey, out _));

        // Admin clicks "Reset / Re-trust Host Key"
        var profile = _profiles.Get();
        profile.Backup.Sftp.TrustedHostKeyFingerprint = string.Empty;
        _profiles.Save(profile);

        Assert.True(provider.VerifyOrPinHostKey(newHostKey, out var err));
        Assert.Null(err);
        Assert.Equal(newFingerprint, _profiles.Get().Backup.Sftp.TrustedHostKeyFingerprint);
    }

    [Theory]
    [InlineData("MD5:aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99")]
    [InlineData("SHA256:short")]
    [InlineData("SHA256:%%%invalid-base64%%%")]
    public void VerifyOrPinHostKey_MalformedPinnedFingerprintOrEmptyHostKey_FailsClosed(string invalidFingerprint)
    {
        var hostKey = RandomNumberGenerator.GetBytes(256);
        var provider = new SftpBackupProvider(_profiles);

        Assert.False(provider.VerifyOrPinHostKey(null, out var emptyErr1));
        Assert.NotNull(emptyErr1);
        Assert.False(provider.VerifyOrPinHostKey(Array.Empty<byte>(), out var emptyErr2));
        Assert.NotNull(emptyErr2);

        var profile = _profiles.Get();
        profile.Backup.Sftp.TrustedHostKeyFingerprint = invalidFingerprint;
        _profiles.Save(profile);

        Assert.False(provider.VerifyOrPinHostKey(hostKey, out var invalidPinErr));
        Assert.NotNull(invalidPinErr);
    }
}
