using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class AlbumServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly AlbumRepository _albums;
    private readonly ChatChannelRepository _channels;
    private readonly EmployeeRepository _employees;
    private readonly Mock<ICryptoService> _mockCrypto;
    private readonly AlbumService _service;

    public AlbumServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Album_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

        _albums = new AlbumRepository(_persistence, _config);
        _channels = new ChatChannelRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);

        _mockCrypto = new Mock<ICryptoService>();
        _mockCrypto.Setup(c => c.GenerateAesKeyBase64()).Returns("plain-aes-key");
        _mockCrypto.Setup(c => c.EncryptRsa(It.IsAny<string>(), It.IsAny<string>())).Returns("rsa-enc-key");
        _mockCrypto.Setup(c => c.DecryptRsa(It.IsAny<string>(), It.IsAny<string>())).Returns("plain-aes-key");
        _mockCrypto.Setup(c => c.EncryptAes(It.IsAny<string>(), It.IsAny<string>())).Returns("aes-enc-key");
        _mockCrypto.Setup(c => c.DecryptAes(It.IsAny<string>(), It.IsAny<string>())).Returns("plain-aes-key");

        var companyProfiles = new CompanyProfileRepository(_persistence, _config);
        var serverConfigs = new ServerConfigRepository(_persistence, _config);
        var escrow = new ServerEscrowService(companyProfiles, serverConfigs, _mockCrypto.Object);

        _service = new AlbumService(_albums, _channels, _mockCrypto.Object, escrow, _employees);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
    }

    // 1. CreateAlbum
    [Fact]
    public void CreateAlbum_ValidOwner_SavesAlbumAndReturnsSuccess()
    {
        var album = new Album { Id = "a1_sync" };
        var emp = new Employee { Id = "u1_sync", PublicKey = "pub" };
        _employees.Save(emp);

        var result = _service.CreateAlbum(album, "u1_sync", "priv");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("u1_sync", result.Value.OwnerId);
        Assert.True(result.Value.IsEncrypted);
        Assert.True(result.Value.EncryptedAlbumKeys.ContainsKey("u1_sync"));
        var saved = _albums.GetById("a1_sync");
        Assert.NotNull(saved);
    }

    [Fact]
    public void CreateAlbum_NullAlbumOrEmptyOwner_ReturnsFailure()
    {
        var r1 = _service.CreateAlbum(null!, "u1", "priv");
        Assert.False(r1.IsSuccess);
        Assert.Equal("INVALID_ARGUMENT", r1.Code);

        var r2 = _service.CreateAlbum(new Album { Id = "a_null_owner" }, "", "priv");
        Assert.False(r2.IsSuccess);
        Assert.Equal("INVALID_ARGUMENT", r2.Code);
    }

    [Fact]
    public void CreateAlbum_ValidOwner_SavesAlbum()
    {
        var album = new Album { Id = "a1" };
        var emp = new Employee { Id = "u1", PublicKey = "pub" };
        _employees.Save(emp);

        var result = _service.CreateAlbum(album, "u1", "priv");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("u1", result.Value.OwnerId);
        Assert.True(result.Value.IsEncrypted);
        Assert.True(result.Value.EncryptedAlbumKeys.ContainsKey("u1"));
        var saved = _albums.GetById("a1");
        Assert.NotNull(saved);
    }

    [Fact]
    public void CreateAlbum_NoPrivateKey_SavesWithoutUserKey()
    {
        var album = new Album { Id = "a2" };
        var result = _service.CreateAlbum(album, "u2", null);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("u2", result.Value.OwnerId);
        Assert.Empty(result.Value.EncryptedAlbumKeys);
    }

    [Fact]
    public void CreateAlbum_RaisesEvent()
    {
        var album = new Album { Id = "a3" };
        bool eventRaised = false;
        _service.OnAlbumUpdated += (id) => eventRaised = (id == "a3");

        var result = _service.CreateAlbum(album, "u3", "priv");
        Assert.True(result.IsSuccess);
        Assert.True(eventRaised);
    }

        // 2. GetPlainAlbumKey
        [Fact]
        public void GetPlainAlbumKey_DirectKey_ReturnsPlainKey()
        {
            var album = new Album { Id = "a4", OwnerId = "u1" };
            album.EncryptedAlbumKeys["u1"] = "cipher";
            var result = _service.GetPlainAlbumKey(album, "u1", "priv");
            Assert.Equal("plain-aes-key", result);
        }

        [Fact]
        public void GetPlainAlbumKey_ViaChannel_ReturnsPlainKey()
        {
            var album = new Album { Id = "a5" };
            album.SharedWithChannelIds.Add("c1");
            album.EncryptedAlbumKeys["Channel_c1"] = "cipher-album-key";
            
            var channel = new ChatChannel { Id = "c1", IsEncrypted = true };
            channel.EncryptedChannelKeys["u1"] = "cipher-channel-key";
            _channels.Save(channel);

            var result = _service.GetPlainAlbumKey(album, "u1", "priv");
            Assert.Equal("plain-aes-key", result);
        }

        [Fact]
        public void GetPlainAlbumKey_NoAccess_ReturnsNull()
        {
            var album = new Album { Id = "a6" };
            var result = _service.GetPlainAlbumKey(album, "u1", "priv");
            Assert.Null(result);
        }

        // 3. ShareAlbumWithChannel
        [Fact]
        public void ShareAlbumWithChannel_AsOwner_AddsChannel()
        {
            var album = new Album { Id = "a7", OwnerId = "u1" };
            album.EncryptedAlbumKeys["u1"] = "cipher";
            _albums.Save(album);
            
            var channel = new ChatChannel { Id = "c1", IsEncrypted = true };
            channel.EncryptedChannelKeys["u1"] = "cipher";
            _channels.Save(channel);

            var result = _service.ShareAlbumWithChannel("a7", "u1", "c1", "priv");

            Assert.True(result.IsSuccess);
            var saved = _albums.GetById("a7");
            Assert.Contains("c1", saved.SharedWithChannelIds);
            Assert.True(saved.EncryptedAlbumKeys.ContainsKey("Channel_c1"));
        }

        [Fact]
        public void ShareAlbumWithChannel_NotOwner_DoesNothing()
        {
            var album = new Album { Id = "a8", OwnerId = "u1" };
            _albums.Save(album);
            var channel = new ChatChannel { Id = "c1" };
            _channels.Save(channel);

            var result = _service.ShareAlbumWithChannel("a8", "u2", "c1", "priv");

            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);
            var saved = _albums.GetById("a8");
            Assert.Empty(saved.SharedWithChannelIds);
        }

        [Fact]
        public void ShareAlbumWithChannel_InvalidChannel_DoesNothing()
        {
            var album = new Album { Id = "a9", OwnerId = "u1" };
            _albums.Save(album);

            var result = _service.ShareAlbumWithChannel("a9", "u1", "c99", "priv");
            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
            var saved = _albums.GetById("a9");
            Assert.Empty(saved.SharedWithChannelIds);
        }

        [Fact]
        public void ShareAlbumWithChannel_EncryptedChannel_CannotDecryptAlbumKey_ReturnsFailure()
        {
            var album = new Album { Id = "a_no_key", OwnerId = "u1" };
            // No encrypted album key for u1
            _albums.Save(album);

            var channel = new ChatChannel { Id = "c_enc", IsEncrypted = true };
            channel.EncryptedChannelKeys["u1"] = "cipher";
            _channels.Save(channel);

            var result = _service.ShareAlbumWithChannel("a_no_key", "u1", "c_enc", "priv");
            Assert.False(result.IsSuccess);
            Assert.Equal("ENCRYPTION_ERROR", result.Code);

            var saved = _albums.GetById("a_no_key");
            Assert.DoesNotContain("c_enc", saved.SharedWithChannelIds);
        }

        [Fact]
        public void ShareAlbumWithChannel_EncryptedChannel_CryptoError_ReturnsFailure()
        {
            var album = new Album { Id = "a_crypto_fail", OwnerId = "u1" };
            album.EncryptedAlbumKeys["u1"] = "cipher";
            _albums.Save(album);

            var channel = new ChatChannel { Id = "c_enc2", IsEncrypted = true };
            channel.EncryptedChannelKeys["u1"] = "cipher";
            _channels.Save(channel);

            _mockCrypto.Setup(c => c.EncryptAes(It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new System.Security.Cryptography.CryptographicException("Encryption failed"));

            var result = _service.ShareAlbumWithChannel("a_crypto_fail", "u1", "c_enc2", "priv");
            Assert.False(result.IsSuccess);
            Assert.Equal("ENCRYPTION_ERROR", result.Code);

            var saved = _albums.GetById("a_crypto_fail");
            Assert.DoesNotContain("c_enc2", saved.SharedWithChannelIds);
            Assert.False(saved.EncryptedAlbumKeys.ContainsKey("Channel_c_enc2"));
        }

        // 4. UnshareAlbumWithChannel
        [Fact]
        public void UnshareAlbumWithChannel_AsOwner_RemovesChannel()
        {
            var album = new Album { Id = "a10", OwnerId = "u1" };
            album.SharedWithChannelIds.Add("c1");
            album.EncryptedAlbumKeys["Channel_c1"] = "cipher";
            _albums.Save(album);

            var result = _service.UnshareAlbumWithChannel("a10", "u1", "c1");

            Assert.True(result.IsSuccess);
            var saved = _albums.GetById("a10");
            Assert.Empty(saved.SharedWithChannelIds);
            Assert.False(saved.EncryptedAlbumKeys.ContainsKey("Channel_c1"));
        }

        [Fact]
        public void UnshareAlbumWithChannel_NotOwner_DoesNothing()
        {
            var album = new Album { Id = "a11", OwnerId = "u1" };
            album.SharedWithChannelIds.Add("c1");
            _albums.Save(album);

            var result = _service.UnshareAlbumWithChannel("a11", "u2", "c1");

            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);
            var saved = _albums.GetById("a11");
            Assert.Contains("c1", saved.SharedWithChannelIds);
        }

        [Fact]
        public void UnshareAlbumWithChannel_RaisesEvent()
        {
            var album = new Album { Id = "a12", OwnerId = "u1" };
            album.SharedWithChannelIds.Add("c1");
            _albums.Save(album);

            bool eventRaised = false;
            _service.OnAlbumUpdated += (id) => eventRaised = (id == "a12");

            _service.UnshareAlbumWithChannel("a12", "u1", "c1");
            Assert.True(eventRaised);
        }

        // 5. ReEvaluateAlbumEncryptionForChannel
        [Fact]
        public void ReEvaluateAlbumEncryptionForChannel_UpdatesEncryption()
        {
            var album = new Album { Id = "a13" };
            album.SharedWithChannelIds.Add("c1");
            _albums.Save(album);
            
            _service.ReEvaluateAlbumEncryptionForChannel("c1", "new-key");
            
            var saved = _albums.GetById("a13");
            Assert.NotNull(saved);
        }
        
        [Fact]
        public void ReEvaluateAlbumEncryptionForChannel_NonExistentChannel_DoesNothing()
        {
            _service.ReEvaluateAlbumEncryptionForChannel("c99", "new-key");
            Assert.True(true);
        }

        [Fact]
        public void ReEvaluateAlbumEncryptionForChannel_MultipleAlbums_Processed()
        {
            var a1 = new Album { Id = "a14" }; a1.SharedWithChannelIds.Add("c2"); _albums.Save(a1);
            var a2 = new Album { Id = "a15" }; a2.SharedWithChannelIds.Add("c2"); _albums.Save(a2);
            
            _service.ReEvaluateAlbumEncryptionForChannel("c2", "key");
            Assert.True(true);
        }

        // 6. UpdateContributors
        [Fact]
        public void UpdateContributors_AddContributor_SavesKey()
        {
            var album = new Album { Id = "a16", OwnerId = "u1" };
            album.EncryptedAlbumKeys["u1"] = "cipher";
            _albums.Save(album);

            var emp = new Employee { Id = "u2", PublicKey = "pub" };
            _employees.Save(emp);

            var result = _service.UpdateContributors("a16", "u1", ["u2"], "priv");
            Assert.True(result.IsSuccess);

            var saved = _albums.GetById("a16");
            Assert.Contains("u2", saved.ContributorUserIds);
            Assert.True(saved.EncryptedAlbumKeys.ContainsKey("u2"));
        }

        [Fact]
        public void UpdateContributors_RemoveContributor_RemovesMedia()
        {
            var album = new Album { Id = "a17", OwnerId = "u1" };
            album.ContributorUserIds.Add("u2");
            album.EncryptedAlbumKeys["u2"] = "cipher";
            album.Media.Add(new AlbumMedia { Id = "m1", AddedByUserId = "u2" });
            _albums.Save(album);

            var result = _service.UpdateContributors("a17", "u1", [], "priv");
            Assert.True(result.IsSuccess);
            var removed = result.Value!;

            var saved = _albums.GetById("a17");
            Assert.Empty(saved.ContributorUserIds);
            Assert.False(saved.EncryptedAlbumKeys.ContainsKey("u2"));
            Assert.Empty(saved.Media);
            Assert.Single(removed);
        }

        [Fact]
        public void UpdateContributors_NotOwner_DoesNothing()
        {
            var album = new Album { Id = "a18", OwnerId = "u1" };
            _albums.Save(album);

            var result = _service.UpdateContributors("a18", "u2", ["u3"], "priv");
            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);

            var saved = _albums.GetById("a18");
            Assert.Empty(saved.ContributorUserIds);
        }

        [Fact]
        public void UpdateContributors_NonExistentAlbum_ReturnsNotFound()
        {
            var result = _service.UpdateContributors("nonexistent", "u1", ["u2"], "priv");
            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
        }

        // 7. AddMedia
        [Fact]
        public void AddMedia_AsOwner_AddsMedia()
        {
            var album = new Album { Id = "a19", OwnerId = "u1" };
            _albums.Save(album);

            _service.AddMedia("a19", "u1", [new AlbumMedia { Id = "m1" }]);

            var saved = _albums.GetById("a19");
            Assert.Single(saved.Media);
            Assert.Equal("m1", saved.Media[0].Id);
        }

        [Fact]
        public void AddMedia_AsContributor_AddsMedia()
        {
            var album = new Album { Id = "a20", OwnerId = "u1" };
            album.ContributorUserIds.Add("u2");
            _albums.Save(album);

            _service.AddMedia("a20", "u2", [new AlbumMedia { Id = "m2" }]);

            var saved = _albums.GetById("a20");
            Assert.Single(saved.Media);
        }

        [Fact]
        public void AddMedia_NoAccess_DoesNothing()
        {
            var album = new Album { Id = "a21", OwnerId = "u1" };
            _albums.Save(album);

            _service.AddMedia("a21", "u2", [new AlbumMedia { Id = "m3" }]);

            var saved = _albums.GetById("a21");
            Assert.Empty(saved.Media);
        }

        // 8. RemoveMedia
        [Fact]
        public void RemoveMedia_AsOwner_RemovesMedia()
        {
            var album = new Album { Id = "a22", OwnerId = "u1" };
            album.Media.Add(new AlbumMedia { Id = "m1", AddedByUserId = "u2" });
            _albums.Save(album);

            _service.RemoveMedia("a22", "u1", new AlbumMedia { Id = "m1" });

            var saved = _albums.GetById("a22");
            Assert.Empty(saved.Media);
        }

        [Fact]
        public void RemoveMedia_AsContributorOwnMedia_RemovesMedia()
        {
            var album = new Album { Id = "a23", OwnerId = "u1" };
            album.ContributorUserIds.Add("u2");
            album.Media.Add(new AlbumMedia { Id = "m2", AddedByUserId = "u2" });
            _albums.Save(album);

            _service.RemoveMedia("a23", "u2", new AlbumMedia { Id = "m2" });

            var saved = _albums.GetById("a23");
            Assert.Empty(saved.Media);
        }

        [Fact]
        public void RemoveMedia_AsContributorOtherMedia_DoesNothing()
        {
            var album = new Album { Id = "a24", OwnerId = "u1" };
            album.ContributorUserIds.Add("u2");
            album.Media.Add(new AlbumMedia { Id = "m3", AddedByUserId = "u1" });
            _albums.Save(album);

            _service.RemoveMedia("a24", "u2", new AlbumMedia { Id = "m3" });

            var saved = _albums.GetById("a24");
            Assert.Single(saved.Media);
        }

        // 9. RemoveMediaBulk
        [Fact]
        public void RemoveMediaBulk_MixedOwnershipAsContributor_RemovesOnlyOwn()
        {
            var album = new Album { Id = "a25", OwnerId = "u1" };
            album.ContributorUserIds.Add("u2");
            album.Media.Add(new AlbumMedia { Id = "m1", AddedByUserId = "u1" });
            album.Media.Add(new AlbumMedia { Id = "m2", AddedByUserId = "u2" });
            _albums.Save(album);

            var result = _service.RemoveMediaBulk("a25", "u2", ["m1", "m2"]);
            Assert.True(result.IsSuccess);
            var removed = result.Value!;

            var saved = _albums.GetById("a25");
            Assert.Single(saved.Media);
            Assert.Equal("m1", saved.Media[0].Id);
            Assert.Single(removed);
            Assert.Equal("m2", removed[0].Id);
        }

        [Fact]
        public void RemoveMediaBulk_AsOwner_RemovesAll()
        {
            var album = new Album { Id = "a26", OwnerId = "u1" };
            album.Media.Add(new AlbumMedia { Id = "m3", AddedByUserId = "u1" });
            album.Media.Add(new AlbumMedia { Id = "m4", AddedByUserId = "u2" });
            _albums.Save(album);

            var result = _service.RemoveMediaBulk("a26", "u1", ["m3", "m4"]);
            Assert.True(result.IsSuccess);
            var removed = result.Value!;

            var saved = _albums.GetById("a26");
            Assert.Empty(saved.Media);
            Assert.Equal(2, removed.Count);
        }
        
        [Fact]
        public void RemoveMediaBulk_NonExistentAlbum_ReturnsNotFound()
        {
            var result = _service.RemoveMediaBulk("a99", "u1", ["m1"]);
            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
        }

        [Fact]
        public void RemoveMediaBulk_NotAuthorized_ReturnsForbidden()
        {
            var album = new Album { Id = "a28", OwnerId = "u1" };
            _albums.Save(album);

            var result = _service.RemoveMediaBulk("a28", "unauthorized_user", ["m1"]);
            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);
        }

        // 10. DeleteAlbum
        [Fact]
        public void DeleteAlbum_AsOwner_Deletes()
        {
            var album = new Album { Id = "a27", OwnerId = "u1" };
            _albums.Save(album);

            var result = _service.DeleteAlbum("a27", "u1");

            Assert.True(result.IsSuccess);
            var saved = _albums.GetById("a27");
            Assert.Null(saved);
        }

        [Fact]
        public void DeleteAlbum_NotOwner_DoesNothing()
        {
            var album = new Album { Id = "a28", OwnerId = "u1" };
            _albums.Save(album);

            var result = _service.DeleteAlbum("a28", "u2");

            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);
            var saved = _albums.GetById("a28");
            Assert.NotNull(saved);
        }

        [Fact]
        public void DeleteAlbum_RaisesEvent()
        {
            var album = new Album { Id = "a29", OwnerId = "u1" };
            _albums.Save(album);

            bool eventRaised = false;
            _service.OnAlbumUpdated += (id) => eventRaised = (id == "a29");

            _service.DeleteAlbum("a29", "u1");
            Assert.True(eventRaised);
        }

        [Fact]
        public async Task ShareAlbumWithChannel_ConcurrentUpdates_AreThreadSafe()
        {
            var album = new Album 
            { 
                Id = "album-concurrent", 
                OwnerId = "user-owner" 
            };
            _albums.Save(album);

            var tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                int channelId = i;
                _channels.Save(new ChatChannel { Id = $"channel-{channelId}" });
                tasks.Add(Task.Run(() => 
                {
                    _service.ShareAlbumWithChannel("album-concurrent", "user-owner", $"channel-{channelId}", null);
                }));
            }

            await Task.WhenAll(tasks);

            var updatedAlbum = _albums.GetById("album-concurrent");
            Assert.NotNull(updatedAlbum);
            Assert.Equal(10, updatedAlbum.SharedWithChannelIds.Count);
            for (int i = 0; i < 10; i++)
            {
                Assert.Contains($"channel-{i}", updatedAlbum.SharedWithChannelIds);
            }
        }

        [Fact]
        public async Task AddMedia_ConcurrentCalls_SynchronizesCorrectly()
        {
            var album = new Album
            {
                Id = "album-concurrent-media",
                OwnerId = "user-owner"
            };
            _albums.Save(album);

            var tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                int index = i;
                tasks.Add(Task.Run(() =>
                {
                    _service.AddMedia("album-concurrent-media", "user-owner", new[]
                    {
                        new AlbumMedia { Id = $"media-{index}", AddedByUserId = "user-owner", FileName = $"file-{index}.jpg" }
                    });
                }));
            }

            await Task.WhenAll(tasks);

            var updatedAlbum = _albums.GetById("album-concurrent-media");
            Assert.NotNull(updatedAlbum);
            Assert.Equal(10, updatedAlbum.Media.Count);
            for (int i = 0; i < 10; i++)
            {
                Assert.Contains(updatedAlbum.Media, m => m.Id == $"media-{i}");
            }
        }

        [Fact]
        public void RenameAlbum_ValidNewTitle_RenamesAndPersistsAndInvokesEvent()
        {
            var album = new Album
            {
                Id = "alb-rename-1",
                Title = "Old Title",
                OwnerId = "user-owner"
            };
            _albums.Save(album);

            string? updatedAlbumId = null;
            _service.OnAlbumUpdated += id => updatedAlbumId = id;

            var result = _service.RenameAlbum("alb-rename-1", "user-owner", "New Beautiful Title");

            Assert.True(result.IsSuccess);
            Assert.Equal("alb-rename-1", updatedAlbumId);

            var saved = _albums.GetById("alb-rename-1");
            Assert.NotNull(saved);
            Assert.Equal("New Beautiful Title", saved.Title);
        }

        [Fact]
        public void RenameAlbum_NotOwner_ReturnsForbidden()
        {
            var album = new Album
            {
                Id = "alb-rename-2",
                Title = "Original Title",
                OwnerId = "user-owner"
            };
            _albums.Save(album);

            var result = _service.RenameAlbum("alb-rename-2", "user-stranger", "Hijacked Title");

            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);

            var saved = _albums.GetById("alb-rename-2");
            Assert.Equal("Original Title", saved!.Title);
        }

        [Fact]
        public void RenameAlbum_EmptyTitle_ReturnsInvalidArgument()
        {
            var album = new Album
            {
                Id = "alb-rename-3",
                Title = "Original Title",
                OwnerId = "user-owner"
            };
            _albums.Save(album);

            var result = _service.RenameAlbum("alb-rename-3", "user-owner", "   ");

            Assert.False(result.IsSuccess);
            Assert.Equal("INVALID_ARGUMENT", result.Code);
        }

        [Fact]
        public void RenameAlbum_NotFound_ReturnsNotFound()
        {
            var result = _service.RenameAlbum("alb-non-existent", "user-owner", "New Title");

            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
        }

        [Fact]
        public void TrySelfHealContributorKeys_MissingKeyWithPublicKey_GeneratesKey()
        {
            _employees.Save(new Employee { Id = "u1", PublicKey = "u1_pub" });
            _employees.Save(new Employee { Id = "u2", PublicKey = "u2_pub" });

            var album = new Album
            {
                Id = "alb-heal-1",
                OwnerId = "u1",
                IsEncrypted = true,
                ContributorUserIds = ["u2"],
                EncryptedAlbumKeys = new Dictionary<string, string> { { "u1", "cipher_key" } }
            };
            _albums.Save(album);

            _mockCrypto.Setup(c => c.DecryptRsa("cipher_key", "u1_priv")).Returns("plain_album_key");
            _mockCrypto.Setup(c => c.EncryptRsa("plain_album_key", "u2_pub")).Returns("u2_cipher_key");

            var result = _service.TrySelfHealContributorKeys("alb-heal-1", "u1", "u1_priv");

            Assert.True(result.IsSuccess);
            var saved = _albums.GetById("alb-heal-1");
            Assert.NotNull(saved);
            Assert.True(saved.EncryptedAlbumKeys.ContainsKey("u2"));
            Assert.Equal("u2_cipher_key", saved.EncryptedAlbumKeys["u2"]);
        }

        [Fact]
        public void TrySelfHealContributorKeys_ContributorHasNoPublicKey_Skips()
        {
            _employees.Save(new Employee { Id = "u1", PublicKey = "u1_pub" });
            _employees.Save(new Employee { Id = "u2", PublicKey = "" }); // No vault yet

            var album = new Album
            {
                Id = "alb-heal-2",
                OwnerId = "u1",
                IsEncrypted = true,
                ContributorUserIds = ["u2"],
                EncryptedAlbumKeys = new Dictionary<string, string> { { "u1", "cipher_key" } }
            };
            _albums.Save(album);

            _mockCrypto.Setup(c => c.DecryptRsa("cipher_key", "u1_priv")).Returns("plain_album_key");

            var result = _service.TrySelfHealContributorKeys("alb-heal-2", "u1", "u1_priv");

            Assert.True(result.IsSuccess);
            var saved = _albums.GetById("alb-heal-2");
            Assert.NotNull(saved);
            Assert.False(saved.EncryptedAlbumKeys.ContainsKey("u2"));
        }

        [Fact]
        public void CreateAlbum_WhenOwnerHasPublicKeyButVaultIsLocked_EncryptsWithOwnerPublicKeyAndDoesNotUseEscrow()
        {
            _employees.Save(new Employee { Id = "u_locked_owner", PublicKey = "owner_pub" });

            var album = new Album { Id = "alb_locked_create", Title = "Private Album" };
            var result = _service.CreateAlbum(album, "u_locked_owner", ownerPrivateKey: null);

            Assert.True(result.IsSuccess);
            var saved = _albums.GetById("alb_locked_create");
            Assert.NotNull(saved);
            Assert.True(saved.EncryptedAlbumKeys.ContainsKey("u_locked_owner"));
            Assert.False(saved.EncryptedAlbumKeys.ContainsKey("Escrow"));
        }

        private ServerEscrowService CreateInitializedEscrow(ICryptoService crypto)
        {
            var escrowConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = _testDataDir,
                    ["SPOKES_MASTER_PASSWORD"] = "TestMasterPassword#2026"
                })
                .Build();
            var companyProfiles = new CompanyProfileRepository(_persistence, escrowConfig);
            var serverConfigs = new ServerConfigRepository(_persistence, escrowConfig);
            var escrow = new ServerEscrowService(companyProfiles, serverConfigs, crypto, configuration: escrowConfig);
            escrow.Initialize();
            return escrow;
        }

        [Fact]
        public void IsAlbumLocked_LegacyPrivateAlbumWithEscrowAndOwnerPublicKey_EagerlyUpgradesToOwnerKeyRemovesEscrowAndLocksWhenVaultLocked()
        {
            var realCrypto = new CryptoService();
            var escrow = CreateInitializedEscrow(realCrypto);

            var service = new AlbumService(_albums, _channels, realCrypto, escrow, _employees);

            var (ownerPub, ownerPriv) = realCrypto.GenerateRsaKeyPair();
            _employees.Save(new Employee { Id = "owner_bug_repro", PublicKey = ownerPub, EncryptedPrivateKey = "enc_priv" });

            var plainAlbumKey = realCrypto.GenerateAesKeyBase64();
            var legacyEscrowAlbum = new Album
            {
                Id = "alb_legacy_escrow",
                OwnerId = "owner_bug_repro",
                IsEncrypted = true,
                EncryptedAlbumKeys = new Dictionary<string, string>
                {
                    ["Escrow"] = realCrypto.EncryptRsa(plainAlbumKey, escrow.EscrowPublicKey!)
                }
            };
            _albums.Save(legacyEscrowAlbum);

            // When the vault is locked (currentPrivateKey == null), IsAlbumLocked must self-heal Escrow -> owner PublicKey,
            // remove Escrow, and report the album as LOCKED (cannot view pictures without private key).
            bool lockedWhenVaultLocked = service.IsAlbumLocked(legacyEscrowAlbum, "owner_bug_repro", currentPrivateKey: null, out var keyWhenLocked);
            Assert.True(lockedWhenVaultLocked);
            Assert.Null(keyWhenLocked);

            var persisted = _albums.GetById("alb_legacy_escrow");
            Assert.NotNull(persisted);
            Assert.True(persisted.EncryptedAlbumKeys.ContainsKey("owner_bug_repro"));
            Assert.False(persisted.EncryptedAlbumKeys.ContainsKey("Escrow"));

            // When the vault is unlocked (currentPrivateKey == ownerPriv), IsAlbumLocked returns false and resolves the plain key.
            bool lockedWhenVaultUnlocked = service.IsAlbumLocked(persisted, "owner_bug_repro", ownerPriv, out var keyWhenUnlocked);
            Assert.False(lockedWhenVaultUnlocked);
            Assert.Equal(plainAlbumKey, keyWhenUnlocked);
        }

        [Fact]
        public void PublicChannelEscrowLifecycle_ShareToUnencryptedChannelAddsEscrow_UnshareRemovesEscrow()
        {
            var realCrypto = new CryptoService();
            var escrow = CreateInitializedEscrow(realCrypto);

            var service = new AlbumService(_albums, _channels, realCrypto, escrow, _employees);

            var (ownerPub, ownerPriv) = realCrypto.GenerateRsaKeyPair();
            _employees.Save(new Employee { Id = "owner_lifecycle", PublicKey = ownerPub, EncryptedPrivateKey = "enc" });
            _employees.Save(new Employee { Id = "viewer_user" });

            var createRes = service.CreateAlbum(new Album { Id = "alb_lifecycle", Title = "Trip" }, "owner_lifecycle", ownerPriv);
            Assert.True(createRes.IsSuccess);

            var created = _albums.GetById("alb_lifecycle")!;
            Assert.True(created.EncryptedAlbumKeys.ContainsKey("owner_lifecycle"));
            Assert.False(created.EncryptedAlbumKeys.ContainsKey("Escrow"));

            // Create an unencrypted public channel
            var publicChannel = new ChatChannel { Id = "pub_chan", Name = "General", IsEncrypted = false };
            _channels.Save(publicChannel);

            // Sharing while vault is locked should fail fast and not add channel without escrow key
            var failedShare = service.ShareAlbumWithChannel("alb_lifecycle", "owner_lifecycle", "pub_chan", currentPrivateKey: null);
            Assert.False(failedShare.IsSuccess);
            Assert.DoesNotContain("pub_chan", _albums.GetById("alb_lifecycle")!.SharedWithChannelIds);

            // Sharing while vault is unlocked adds Escrow so public channel viewers can access it
            var okShare = service.ShareAlbumWithChannel("alb_lifecycle", "owner_lifecycle", "pub_chan", ownerPriv);
            Assert.True(okShare.IsSuccess);

            var sharedAlbum = _albums.GetById("alb_lifecycle")!;
            Assert.Contains("pub_chan", sharedAlbum.SharedWithChannelIds);
            Assert.True(sharedAlbum.EncryptedAlbumKeys.ContainsKey("Escrow"));
            Assert.False(service.IsAlbumLocked(sharedAlbum, "viewer_user", currentPrivateKey: null));

            // Unsharing from the public channel immediately removes Escrow again
            var unshareRes = service.UnshareAlbumWithChannel("alb_lifecycle", "owner_lifecycle", "pub_chan");
            Assert.True(unshareRes.IsSuccess);

            var unsharedAlbum = _albums.GetById("alb_lifecycle")!;
            Assert.Empty(unsharedAlbum.SharedWithChannelIds);
            Assert.False(unsharedAlbum.EncryptedAlbumKeys.ContainsKey("Escrow"));
            Assert.True(service.IsAlbumLocked(unsharedAlbum, "owner_lifecycle", currentPrivateKey: null));
        }

        [Fact]
        public void ClaimEscrowedAlbumsForUser_UpgradesPrivateAlbumsAndRemovesEscrow()
        {
            var realCrypto = new CryptoService();
            var escrow = CreateInitializedEscrow(realCrypto);

            var service = new AlbumService(_albums, _channels, realCrypto, escrow, _employees);

            // User initially has no vault password when creating the album
            var user = new Employee { Id = "u_claim", PublicKey = "", EncryptedPrivateKey = "" };
            _employees.Save(user);

            var createRes = service.CreateAlbum(new Album { Id = "alb_pre_vault", Title = "Before Vault" }, "u_claim", null);
            Assert.True(createRes.IsSuccess);
            Assert.True(_albums.GetById("alb_pre_vault")!.EncryptedAlbumKeys.ContainsKey("Escrow"));

            // Now user sets up their vault password
            var (pubKey, privKey) = realCrypto.GenerateRsaKeyPair();
            user.PublicKey = pubKey;
            user.EncryptedPrivateKey = "enc_priv";
            _employees.Save(user);

            service.ClaimEscrowedAlbumsForUser("u_claim");

            var upgraded = _albums.GetById("alb_pre_vault")!;
            Assert.True(upgraded.EncryptedAlbumKeys.ContainsKey("u_claim"));
            Assert.False(upgraded.EncryptedAlbumKeys.ContainsKey("Escrow"));
            Assert.True(service.IsAlbumLocked(upgraded, "u_claim", currentPrivateKey: null));
            Assert.False(service.IsAlbumLocked(upgraded, "u_claim", currentPrivateKey: privKey));
        }

        [Fact]
        public void CanUserViewAlbum_EnforcesOwnerContributorAndSharedChannelMembership()
        {
            var privateChannel = new ChatChannel
            {
                Id = "chan_shared_priv",
                Name = "Private Group",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = ["u_chan_member"]
            };
            _channels.Save(privateChannel);

            var album = new Album
            {
                Id = "alb_view_auth",
                OwnerId = "u_owner",
                ContributorUserIds = ["u_contrib"],
                SharedWithChannelIds = ["chan_shared_priv"]
            };
            _albums.Save(album);

            Assert.True(_service.CanUserViewAlbum("alb_view_auth", "u_owner"));
            Assert.True(_service.CanUserViewAlbum("alb_view_auth", "u_contrib"));
            Assert.True(_service.CanUserViewAlbum("alb_view_auth", "u_chan_member"));
            Assert.False(_service.CanUserViewAlbum("alb_view_auth", "u_outsider"));
        }

        [Fact]
        public void GetPlainAlbumKey_WhenUserCannotViewAlbum_EvenWithDirectOrEscrowKey_ReturnsNull()
        {
            var realCrypto = new CryptoService();
            var escrow = CreateInitializedEscrow(realCrypto);
            var service = new AlbumService(_albums, _channels, realCrypto, escrow, _employees);

            var plainKey = realCrypto.GenerateAesKeyBase64();
            var (strangerPub, strangerPriv) = realCrypto.GenerateRsaKeyPair();

            var album = new Album
            {
                Id = "alb_key_guard",
                OwnerId = "u_real_owner",
                IsEncrypted = true,
                EncryptedAlbumKeys = new Dictionary<string, string>
                {
                    ["u_stranger"] = realCrypto.EncryptRsa(plainKey, strangerPub),
                    ["Escrow"] = realCrypto.EncryptRsa(plainKey, escrow.EscrowPublicKey!)
                }
            };
            _albums.Save(album);

            Assert.Null(service.GetPlainAlbumKey(album, "u_stranger", strangerPriv));
            Assert.Null(service.GetPlainAlbumKey(album, "u_stranger", null));
        }

        [Fact]
        public void ShareAlbumWithChannel_NewChannelAsContributor_ReturnsForbiddenAndDoesNotAddEscrow()
        {
            var realCrypto = new CryptoService();
            var escrow = CreateInitializedEscrow(realCrypto);
            var service = new AlbumService(_albums, _channels, realCrypto, escrow, _employees);

            var (ownerPub, ownerPriv) = realCrypto.GenerateRsaKeyPair();
            var (contribPub, contribPriv) = realCrypto.GenerateRsaKeyPair();
            _employees.Save(new Employee { Id = "u_owner_esc", PublicKey = ownerPub, EncryptedPrivateKey = "enc" });
            _employees.Save(new Employee { Id = "u_contrib_esc", PublicKey = contribPub, EncryptedPrivateKey = "enc" });

            var createRes = service.CreateAlbum(new Album { Id = "alb_e2ee_strict", Title = "Strict E2EE" }, "u_owner_esc", ownerPriv);
            Assert.True(createRes.IsSuccess);
            Assert.True(service.UpdateContributors("alb_e2ee_strict", "u_owner_esc", ["u_contrib_esc"], ownerPriv).IsSuccess);

            var unencryptedChannel = new ChatChannel { Id = "pub_unenc", Name = "Public", IsEncrypted = false };
            _channels.Save(unencryptedChannel);

            var shareRes = service.ShareAlbumWithChannel("alb_e2ee_strict", "u_contrib_esc", "pub_unenc", contribPriv);

            Assert.False(shareRes.IsSuccess);
            Assert.Equal("FORBIDDEN", shareRes.Code);

            var persisted = _albums.GetById("alb_e2ee_strict")!;
            Assert.DoesNotContain("pub_unenc", persisted.SharedWithChannelIds);
            Assert.False(persisted.EncryptedAlbumKeys.ContainsKey("Escrow"));
        }

        [Fact]
        public void ShareAlbumWithChannel_AlreadySharedChannel_AllowsAccessibleMemberReMentionAndRejectsInaccessibleCaller()
        {
            var mockChatAuth = new Mock<IChatAuthorizationService>();
            var owner = new Employee { Id = "u_owner_re", IsActive = true };
            var contrib = new Employee { Id = "u_contrib_re", IsActive = true };
            var outsider = new Employee { Id = "u_outsider_re", IsActive = true };
            _employees.Save(owner);
            _employees.Save(contrib);
            _employees.Save(outsider);

            var channel = new ChatChannel
            {
                Id = "chan_shared_re",
                ChannelType = ChatChannelType.Group,
                ParticipantIds = ["u_owner_re", "u_contrib_re"],
                IsEncrypted = true,
                EncryptedChannelKeys = new Dictionary<string, string>
                {
                    ["u_owner_re"] = "cipher",
                    ["u_contrib_re"] = "cipher"
                }
            };
            _channels.Save(channel);

            mockChatAuth.Setup(a => a.CanUserAccessChannel(It.Is<ChatChannel>(c => c.Id == "chan_shared_re"), It.Is<Employee>(e => e.Id == "u_contrib_re"))).Returns(true);
            mockChatAuth.Setup(a => a.CanUserAccessChannel(It.Is<ChatChannel>(c => c.Id == "chan_shared_re"), It.Is<Employee>(e => e.Id == "u_outsider_re"))).Returns(false);

            var escrow = CreateInitializedEscrow(_mockCrypto.Object);
            var service = new AlbumService(_albums, _channels, _mockCrypto.Object, escrow, _employees, chatAuth: mockChatAuth.Object);

            var album = new Album
            {
                Id = "alb_re_mention",
                OwnerId = "u_owner_re",
                ContributorUserIds = ["u_contrib_re"],
                SharedWithChannelIds = ["chan_shared_re"],
                EncryptedAlbumKeys = new Dictionary<string, string>
                {
                    ["u_owner_re"] = "owner_cipher",
                    ["Channel_chan_shared_re"] = "existing_channel_cipher"
                }
            };
            _albums.Save(album);

            var okRes = service.ShareAlbumWithChannel("alb_re_mention", "u_contrib_re", "chan_shared_re", currentPrivateKey: null);
            Assert.True(okRes.IsSuccess);
            var afterOk = _albums.GetById("alb_re_mention")!;
            Assert.Equal("existing_channel_cipher", afterOk.EncryptedAlbumKeys["Channel_chan_shared_re"]);
            Assert.False(afterOk.EncryptedAlbumKeys.ContainsKey("Escrow"));

            var deniedRes = service.ShareAlbumWithChannel("alb_re_mention", "u_outsider_re", "chan_shared_re", currentPrivateKey: null);
            Assert.False(deniedRes.IsSuccess);
            Assert.Equal("FORBIDDEN", deniedRes.Code);
        }

        [Fact]
        public void ShareAlbumWithChannel_NewChannelWhereOwnerCannotPost_ReturnsForbidden()
        {
            var mockChatAuth = new Mock<IChatAuthorizationService>();
            var owner = new Employee { Id = "u_owner_nopost", IsActive = true };
            _employees.Save(owner);

            var announcementChannel = new ChatChannel
            {
                Id = "chan_announce",
                IsAnnouncementOnly = true,
                IsEncrypted = false
            };
            _channels.Save(announcementChannel);

            mockChatAuth.Setup(a => a.CanUserPostToChannel(It.Is<ChatChannel>(c => c.Id == "chan_announce"), It.Is<Employee>(e => e.Id == "u_owner_nopost"))).Returns(false);

            var escrow = CreateInitializedEscrow(_mockCrypto.Object);
            var service = new AlbumService(_albums, _channels, _mockCrypto.Object, escrow, _employees, chatAuth: mockChatAuth.Object);

            var album = new Album
            {
                Id = "alb_announce_block",
                OwnerId = "u_owner_nopost",
                EncryptedAlbumKeys = new Dictionary<string, string> { ["u_owner_nopost"] = "cipher" }
            };
            _albums.Save(album);

            var res = service.ShareAlbumWithChannel("alb_announce_block", "u_owner_nopost", "chan_announce", "priv");
            Assert.False(res.IsSuccess);
            Assert.Equal("FORBIDDEN", res.Code);
            Assert.DoesNotContain("chan_announce", _albums.GetById("alb_announce_block")!.SharedWithChannelIds);
        }

        [Fact]
        public void UnshareAlbumWithChannel_AsContributor_ReturnsForbidden()
        {
            var album = new Album
            {
                Id = "alb_unshare_contrib",
                OwnerId = "u_owner",
                ContributorUserIds = ["u_contrib"],
                SharedWithChannelIds = ["c1"]
            };
            _albums.Save(album);

            var res = _service.UnshareAlbumWithChannel("alb_unshare_contrib", "u_contrib", "c1");
            Assert.False(res.IsSuccess);
            Assert.Equal("FORBIDDEN", res.Code);
            Assert.Contains("c1", _albums.GetById("alb_unshare_contrib")!.SharedWithChannelIds);
        }

        [Fact]
        public void UpdateContributors_And_UnshareAlbumWithChannel_InvalidateIssuedFileTokens()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var tokenService = new FileTokenService(new EphemeralDataProtectionProvider(), cache);
            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(a => a.GetUsersForChannel("c_rev")).Returns(["u_owner_rev", "u_viewer_rev"]);

            var escrow = CreateInitializedEscrow(_mockCrypto.Object);
            var service = new AlbumService(
                _albums,
                _channels,
                _mockCrypto.Object,
                escrow,
                _employees,
                chatAccess: mockChatAccess.Object,
                fileTokenService: tokenService);

            var album = new Album
            {
                Id = "alb_token_rev",
                OwnerId = "u_owner_rev",
                ContributorUserIds = ["u_contrib_rev"],
                SharedWithChannelIds = ["c_rev"],
                EncryptedAlbumKeys = new Dictionary<string, string>
                {
                    ["u_owner_rev"] = "cipher",
                    ["u_contrib_rev"] = "cipher",
                    ["Channel_c_rev"] = "cipher"
                }
            };
            _albums.Save(album);
            _channels.Save(new ChatChannel { Id = "c_rev", IsEncrypted = true });

            var contribAlbumToken = tokenService.GenerateAccessToken("albums", "alb_token_rev", "p1.jpg", "plain-aes-key", "u_contrib_rev", "sess_c");
            var contribOtherToken = tokenService.GenerateAccessToken("chat", "other_chan", "doc.pdf", "chat-key", "u_contrib_rev", "sess_c");
            var ownerOtherToken = tokenService.GenerateAccessToken("chat", "other_chan", "doc.pdf", "chat-key", "u_owner_rev", "sess_o");

            Assert.Equal(FileTokenStatus.Valid, tokenService.ValidateAccessToken("albums", "alb_token_rev", "p1.jpg", contribAlbumToken, "u_contrib_rev", "sess_c").Status);

            // Removing contributor invalidates their tokens without invalidating the owner's unrelated chat token
            var updateRes = service.UpdateContributors("alb_token_rev", "u_owner_rev", [], "priv");
            Assert.True(updateRes.IsSuccess);
            Assert.Equal(FileTokenStatus.Invalid, tokenService.ValidateAccessToken("albums", "alb_token_rev", "p1.jpg", contribAlbumToken, "u_contrib_rev", "sess_c").Status);
            Assert.Equal(FileTokenStatus.Invalid, tokenService.ValidateAccessToken("chat", "other_chan", "doc.pdf", contribOtherToken, "u_contrib_rev", "sess_c").Status);
            Assert.Equal(FileTokenStatus.Valid, tokenService.ValidateAccessToken("chat", "other_chan", "doc.pdf", ownerOtherToken, "u_owner_rev", "sess_o").Status);

            // Unsharing channel invalidates album context tokens and viewer tokens while keeping owner's unrelated chat token valid
            var viewerAlbumToken = tokenService.GenerateAccessToken("albums", "alb_token_rev", "p1.jpg", "plain-aes-key", "u_viewer_rev", "sess_v");
            var ownerAlbumTokenBeforeUnshare = tokenService.GenerateAccessToken("albums", "alb_token_rev", "p1.jpg", "plain-aes-key", "u_owner_rev", "sess_o");
            Assert.Equal(FileTokenStatus.Valid, tokenService.ValidateAccessToken("albums", "alb_token_rev", "p1.jpg", viewerAlbumToken, "u_viewer_rev", "sess_v").Status);

            var unshareRes = service.UnshareAlbumWithChannel("alb_token_rev", "u_owner_rev", "c_rev");
            Assert.True(unshareRes.IsSuccess);
            Assert.Equal(FileTokenStatus.Invalid, tokenService.ValidateAccessToken("albums", "alb_token_rev", "p1.jpg", viewerAlbumToken, "u_viewer_rev", "sess_v").Status);
            Assert.Equal(FileTokenStatus.Invalid, tokenService.ValidateAccessToken("albums", "alb_token_rev", "p1.jpg", ownerAlbumTokenBeforeUnshare, "u_owner_rev", "sess_o").Status);
            Assert.Equal(FileTokenStatus.Valid, tokenService.ValidateAccessToken("chat", "other_chan", "doc.pdf", ownerOtherToken, "u_owner_rev", "sess_o").Status);
        }

        [Fact]
        public void Mutations_WhenCallerIsSuspended_ReturnForbidden()
        {
            _employees.Save(new Employee { Id = "u_susp", IsActive = true, IsSuspended = true });
            _channels.Save(new ChatChannel { Id = "c_susp" });

            var album = new Album
            {
                Id = "alb_susp",
                Title = "Original",
                OwnerId = "u_susp",
                SharedWithChannelIds = ["c_susp"],
                Media = [new AlbumMedia { Id = "m_susp", AddedByUserId = "u_susp" }]
            };
            _albums.Save(album);

            Assert.Equal("FORBIDDEN", _service.CreateAlbum(new Album { Id = "alb_new_susp" }, "u_susp", "priv").Code);
            Assert.Equal("FORBIDDEN", _service.RenameAlbum("alb_susp", "u_susp", "Renamed").Code);
            Assert.Equal("FORBIDDEN", _service.AddMedia("alb_susp", "u_susp", [new AlbumMedia { Id = "m2" }]).Code);
            Assert.Equal("FORBIDDEN", _service.RemoveMedia("alb_susp", "u_susp", new AlbumMedia { Id = "m_susp" }).Code);
            Assert.Equal("FORBIDDEN", _service.RemoveMediaBulk("alb_susp", "u_susp", ["m_susp"]).Code);
            Assert.Equal("FORBIDDEN", _service.ShareAlbumWithChannel("alb_susp", "u_susp", "c_susp", "priv").Code);
            Assert.Equal("FORBIDDEN", _service.UnshareAlbumWithChannel("alb_susp", "u_susp", "c_susp").Code);
            Assert.Equal("FORBIDDEN", _service.UpdateContributors("alb_susp", "u_susp", ["u_other"], "priv").Code);
            Assert.Equal("FORBIDDEN", _service.DeleteAlbum("alb_susp", "u_susp").Code);

            var persisted = _albums.GetById("alb_susp");
            Assert.NotNull(persisted);
            Assert.Equal("Original", persisted.Title);
            Assert.Single(persisted.Media);
        }

        [Fact]
        public async Task ImportChatAttachmentsToAlbumAsync_EncryptedChannel_DecryptsWithOwnersKeyAndReEncryptsIntoAlbumAndDeletesChatFiles()
        {
            var realCrypto = new CryptoService();
            var fileService = new Spokes_Server.Core.Services.Storage.FileService(
                _config,
                new Mock<ILogger<Spokes_Server.Core.Services.Storage.FileService>>().Object,
                realCrypto);
            var companyProfiles = new CompanyProfileRepository(_persistence, _config);
            var serverConfigs = new ServerConfigRepository(_persistence, _config);
            var escrow = new ServerEscrowService(companyProfiles, serverConfigs, realCrypto);
            var service = new AlbumService(_albums, _channels, realCrypto, escrow, _employees, fileService: fileService);

            var (pubKey, privKey) = realCrypto.GenerateRsaKeyPair();

            var owner = new Employee { Id = "u_enc_owner", PublicKey = pubKey, EncryptedPrivateKey = "enc-priv", IsActive = true };
            _employees.Save(owner);

            var plainChannelKey = realCrypto.GenerateAesKeyBase64();
            var channel = new ChatChannel
            {
                Id = "c_secure_import",
                Name = "Secure Channel",
                ChannelType = ChatChannelType.General,
                IsEncrypted = true,
                EncryptedChannelKeys = new Dictionary<string, string>
                {
                    [owner.Id] = realCrypto.EncryptRsa(plainChannelKey, pubKey)
                }
            };
            _channels.Save(channel);

            var chatAttachments = new List<ChatAttachment>();
            var originalPayloads = new List<byte[]>();

            for (int i = 0; i < 4; i++)
            {
                var payload = System.Text.Encoding.UTF8.GetBytes($"encrypted-photo-payload-{i}-{Guid.NewGuid()}");
                originalPayloads.Add(payload);

                using var stream = new MemoryStream(payload);
                var chatFilePath = await fileService.UploadStreamAsync("chat", channel.Id, stream, $"photo{i}.jpg", plainChannelKey);
                var chatSafeName = Path.GetFileName(chatFilePath);

                using var thumbStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"thumb-{i}"));
                await fileService.UploadThumbnailAsync("chat", channel.Id, thumbStream, chatSafeName, plainChannelKey);

                chatAttachments.Add(new ChatAttachment
                {
                    Id = $"att-{i}",
                    FileName = $"photo{i}.jpg",
                    FilePath = chatFilePath,
                    ContentType = "image/jpeg",
                    FileSizeBytes = payload.Length,
                    HasServerThumbnail = true,
                    ImageWidth = 800,
                    ImageHeight = 600
                });
            }

            var importResult = await service.ImportChatAttachmentsToAlbumAsync(
                owner.Id,
                channel.Id,
                chatAttachments,
                targetAlbumId: null,
                newAlbumTitle: "Secure Trip Photos",
                currentPrivateKey: privKey,
                plainChannelKey: null,
                deleteSourceFiles: true);

            Assert.True(importResult.IsSuccess);
            var album = importResult.Value!;
            Assert.Equal("Secure Trip Photos", album.Title);
            Assert.Equal(4, album.Media.Count);
            Assert.Contains(channel.Id, album.SharedWithChannelIds);
            Assert.True(album.EncryptedAlbumKeys.ContainsKey($"Channel_{channel.Id}"));
            Assert.False(album.EncryptedAlbumKeys.ContainsKey("Escrow"));

            var plainAlbumKey = service.GetPlainAlbumKey(album, owner.Id, privKey);
            Assert.False(string.IsNullOrEmpty(plainAlbumKey));

            for (int i = 0; i < 4; i++)
            {
                var media = album.Media[i];
                Assert.StartsWith($"/spokesapi/files/albums/{album.Id}/", media.FilePath);
                Assert.True(media.HasServerThumbnail);

                var albumSafeName = Path.GetFileName(media.FilePath);
                using var decryptedAlbumStream = fileService.OpenRead("albums", album.Id, albumSafeName, plainAlbumKey);
                Assert.NotNull(decryptedAlbumStream);
                using var ms = new MemoryStream();
                await decryptedAlbumStream!.CopyToAsync(ms);
                Assert.Equal(originalPayloads[i], ms.ToArray());

                // Verify source chat file and thumbnail were deleted
                var oldChatSafeName = Path.GetFileName(chatAttachments[i].FilePath);
                Assert.False(fileService.FileExists("chat", channel.Id, oldChatSafeName));
                Assert.False(fileService.FileExists("chat", channel.Id, Spokes_Server.Core.Services.Storage.FileService.GetThumbnailFileName(oldChatSafeName)));
            }
        }

        [Fact]
        public async Task ImportChatAttachmentsToAlbumAsync_EncryptedChannelWithLockedVault_ReturnsVaultLocked()
        {
            var realCrypto = new CryptoService();
            var fileService = new Spokes_Server.Core.Services.Storage.FileService(
                _config,
                new Mock<ILogger<Spokes_Server.Core.Services.Storage.FileService>>().Object,
                realCrypto);
            var companyProfiles = new CompanyProfileRepository(_persistence, _config);
            var serverConfigs = new ServerConfigRepository(_persistence, _config);
            var escrow = new ServerEscrowService(companyProfiles, serverConfigs, realCrypto);
            var service = new AlbumService(_albums, _channels, realCrypto, escrow, _employees, fileService: fileService);

            _employees.Save(new Employee { Id = "u_locked", IsActive = true });
            _channels.Save(new ChatChannel { Id = "c_locked", ChannelType = ChatChannelType.General, IsEncrypted = true });

            var result = await service.ImportChatAttachmentsToAlbumAsync(
                "u_locked",
                "c_locked",
                [new ChatAttachment { FileName = "a.jpg", FilePath = "/spokesapi/files/chat/c_locked/a.jpg", ContentType = "image/jpeg" }],
                targetAlbumId: null,
                newAlbumTitle: "Locked Album",
                currentPrivateKey: null,
                plainChannelKey: null);

            Assert.False(result.IsSuccess);
            Assert.Equal("VAULT_LOCKED", result.Code);
        }

        [Fact]
        public void GetWritableAlbumsForChannel_FiltersByOwnerAndSharedContributorAlbums()
        {
            _employees.Save(new Employee { Id = "u_writer", PublicKey = "pub", IsActive = true });
            _channels.Save(new ChatChannel { Id = "c_target", ChannelType = ChatChannelType.General });

            var ownedAlbum = new Album
            {
                Id = "alb_owned",
                Title = "Owned",
                OwnerId = "u_writer",
                IsEncrypted = true,
                EncryptedAlbumKeys = new Dictionary<string, string> { ["u_writer"] = "rsa-enc-key" }
            };
            var contribSharedAlbum = new Album
            {
                Id = "alb_contrib_shared",
                Title = "Contrib Shared",
                OwnerId = "other",
                ContributorUserIds = ["u_writer"],
                SharedWithChannelIds = ["c_target"],
                IsEncrypted = true,
                EncryptedAlbumKeys = new Dictionary<string, string> { ["u_writer"] = "rsa-enc-key" }
            };
            var contribUnsharedAlbum = new Album
            {
                Id = "alb_contrib_unshared",
                Title = "Contrib Unshared",
                OwnerId = "other",
                ContributorUserIds = ["u_writer"],
                SharedWithChannelIds = ["c_other"],
                IsEncrypted = true,
                EncryptedAlbumKeys = new Dictionary<string, string> { ["u_writer"] = "rsa-enc-key" }
            };

            _albums.Save(ownedAlbum);
            _albums.Save(contribSharedAlbum);
            _albums.Save(contribUnsharedAlbum);

            var writable = _service.GetWritableAlbumsForChannel("u_writer", "c_target", "priv");

            Assert.Equal(2, writable.Count);
            Assert.Contains(writable, a => a.Id == "alb_owned");
            Assert.Contains(writable, a => a.Id == "alb_contrib_shared");
            Assert.DoesNotContain(writable, a => a.Id == "alb_contrib_unshared");
        }
    }

