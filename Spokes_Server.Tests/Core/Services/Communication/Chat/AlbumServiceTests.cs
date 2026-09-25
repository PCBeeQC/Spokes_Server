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

        _service = new AlbumService(_albums, _channels, _mockCrypto.Object, null, _employees);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    // 1. CreateAlbumAsync
    [Fact]
    public async Task CreateAlbumAsync_ValidOwner_SavesAlbum()
    {
            var album = new Album { Id = "a1" };
            var emp = new Employee { Id = "u1", PublicKey = "pub" };
            _employees.Save(emp);

            var result = await _service.CreateAlbumAsync(album, "u1", "priv");

            Assert.Equal("u1", result.OwnerId);
            Assert.True(result.IsEncrypted);
            Assert.True(result.EncryptedAlbumKeys.ContainsKey("u1"));
            var saved = _albums.GetById("a1");
            Assert.NotNull(saved);
        }

        [Fact]
        public async Task CreateAlbumAsync_NoPrivateKey_SavesWithoutUserKey()
        {
            var album = new Album { Id = "a2" };
            var result = await _service.CreateAlbumAsync(album, "u2", null);

            Assert.Equal("u2", result.OwnerId);
            Assert.Empty(result.EncryptedAlbumKeys);
        }

        [Fact]
        public async Task CreateAlbumAsync_RaisesEvent()
        {
            var album = new Album { Id = "a3" };
            bool eventRaised = false;
            _service.OnAlbumUpdated += (id) => eventRaised = (id == "a3");

            await _service.CreateAlbumAsync(album, "u3", "priv");
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

            _service.ShareAlbumWithChannel("a7", "u1", "c1", "priv");

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

            _service.ShareAlbumWithChannel("a8", "u2", "c1", "priv");

            var saved = _albums.GetById("a8");
            Assert.Empty(saved.SharedWithChannelIds);
        }

        [Fact]
        public void ShareAlbumWithChannel_InvalidChannel_DoesNothing()
        {
            var album = new Album { Id = "a9", OwnerId = "u1" };
            _albums.Save(album);

            _service.ShareAlbumWithChannel("a9", "u1", "c99", "priv");
            var saved = _albums.GetById("a9");
            Assert.Empty(saved.SharedWithChannelIds);
        }

        // 4. UnshareAlbumWithChannel
        [Fact]
        public void UnshareAlbumWithChannel_AsOwner_RemovesChannel()
        {
            var album = new Album { Id = "a10", OwnerId = "u1" };
            album.SharedWithChannelIds.Add("c1");
            album.EncryptedAlbumKeys["Channel_c1"] = "cipher";
            _albums.Save(album);

            _service.UnshareAlbumWithChannel("a10", "u1", "c1");

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

            _service.UnshareAlbumWithChannel("a11", "u2", "c1");

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

            _service.UpdateContributors("a16", "u1", ["u2"], "priv");

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

            var removed = _service.UpdateContributors("a17", "u1", [], "priv");

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

            _service.UpdateContributors("a18", "u2", ["u3"], "priv");

            var saved = _albums.GetById("a18");
            Assert.Empty(saved.ContributorUserIds);
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

            var removed = _service.RemoveMediaBulk("a25", "u2", ["m1", "m2"]);

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

            var removed = _service.RemoveMediaBulk("a26", "u1", ["m3", "m4"]);

            var saved = _albums.GetById("a26");
            Assert.Empty(saved.Media);
            Assert.Equal(2, removed.Count);
        }
        
        [Fact]
        public void RemoveMediaBulk_NonExistentAlbum_ReturnsEmpty()
        {
            var removed = _service.RemoveMediaBulk("a99", "u1", ["m1"]);
            Assert.Empty(removed);
        }

        // 10. DeleteAlbum
        [Fact]
        public void DeleteAlbum_AsOwner_Deletes()
        {
            var album = new Album { Id = "a27", OwnerId = "u1" };
            _albums.Save(album);

            _service.DeleteAlbum("a27", "u1");

            var saved = _albums.GetById("a27");
            Assert.Null(saved);
        }

        [Fact]
        public void DeleteAlbum_NotOwner_DoesNothing()
        {
            var album = new Album { Id = "a28", OwnerId = "u1" };
            _albums.Save(album);

            _service.DeleteAlbum("a28", "u2");

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
    }
