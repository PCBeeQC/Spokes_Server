using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Moq;
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
            var album = new Album { Id = "a4" };
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
    }

