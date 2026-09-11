using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication
{
    public class AlbumServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _persistence;
        private readonly AlbumRepository _albums;
        private readonly ChatChannelRepository _channels;
        private readonly AlbumService _albumService;

        public AlbumServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Album_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            _persistence.StartAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            
            _albums = new AlbumRepository(_persistence, config);
            _channels = new ChatChannelRepository(_persistence, config);
            var employees = new Spokes_Server.Core.Data.Repositories.HR.EmployeeRepository(_persistence, config);
            var crypto = new Mock<Spokes_Server.Core.Services.Security.ICryptoService>().Object;
            var systemConfig = new Spokes_Server.Core.Data.Repositories.Core.SystemConfigRepository(_persistence, config);
            var escrow = new Spokes_Server.Core.Services.Security.ServerEscrowService(null!, crypto);
            
            _albumService = new AlbumService(_albums, _channels, crypto, escrow, employees);
        }

        public void Dispose()
        {
            _persistence.StopAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch { }
            }
        }

        [Fact]
        public async Task ShareAlbumWithChannel_ConcurrentUpdates_AreThreadSafe()
        {
            // Create an album
            var album = new Spokes_Server.Core.Models.Communication.Album 
            { 
                Id = "album-concurrent", 
                OwnerId = "user-owner" 
            };
            _albums.Save(album);

            // We will simulate 10 concurrent requests to share with 10 different channels
            var tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                int channelId = i;
                _channels.Save(new Spokes_Server.Core.Models.Communication.ChatChannel { Id = $"channel-{channelId}" });
                tasks.Add(Task.Run(() => 
                {
                    _albumService.ShareAlbumWithChannel("album-concurrent", "user-owner", $"channel-{channelId}", null);
                }));
            }

            // If it's not thread-safe, this will throw InvalidOperationException (collection modified)
            await Task.WhenAll(tasks);

            // Verify in-memory
            var updatedAlbum = _albums.GetById("album-concurrent");
            Assert.Equal(10, updatedAlbum.SharedWithChannelIds.Count);
            for (int i = 0; i < 10; i++)
            {
                Assert.Contains($"channel-{i}", updatedAlbum.SharedWithChannelIds);
            }

            // Verify on disk by loading a new repository instance
            // Give the background writer a moment to flush the queue
            System.Threading.Thread.Sleep(200);
            
            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            var newRepo = new AlbumRepository(_persistence, config);
            newRepo.LoadFromDisk();
            
            var diskAlbum = newRepo.GetById("album-concurrent");
            Assert.NotNull(diskAlbum);
            Assert.Equal(10, diskAlbum.SharedWithChannelIds.Count);
            for (int i = 0; i < 10; i++)
            {
                Assert.Contains($"channel-{i}", diskAlbum.SharedWithChannelIds);
            }
        }

        [Fact]
        public void RemoveMediaBulk_DeletesMultipleItemsAtOnce()
        {
            var album = new Spokes_Server.Core.Models.Communication.Album 
            { 
                Id = "album-bulk", 
                OwnerId = "user-owner" 
            };
            var m1 = new AlbumMedia { Id = "m1" };
            var m2 = new AlbumMedia { Id = "m2" };
            var m3 = new AlbumMedia { Id = "m3" };
            album.Media = new List<AlbumMedia> { m1, m2, m3 };
            _albums.Save(album);

            var removed = _albumService.RemoveMediaBulk("album-bulk", "user-owner", new[] { "m1", "m3" });
            
            Assert.Equal(2, removed.Count);
            
            var updatedAlbum = _albums.GetById("album-bulk");
            Assert.Single(updatedAlbum.Media);
            Assert.Equal("m2", updatedAlbum.Media[0].Id);
        }
        [Fact]
        public void RemoveMediaBulk_Contributor_CanOnlyDeleteOwnMedia()
        {
            var album = new Spokes_Server.Core.Models.Communication.Album 
            { 
                Id = "album-contrib-del", 
                OwnerId = "user-owner",
                ContributorUserIds = new List<string> { "user-contrib" }
            };
            var m1 = new AlbumMedia { Id = "m1", AddedByUserId = "user-owner" };
            var m2 = new AlbumMedia { Id = "m2", AddedByUserId = "user-contrib" };
            album.Media = new List<AlbumMedia> { m1, m2 };
            _albums.Save(album);

            var removed = _albumService.RemoveMediaBulk("album-contrib-del", "user-contrib", new[] { "m1", "m2" });
            
            Assert.Single(removed);
            Assert.Equal("m2", removed[0].Id);
            
            var updatedAlbum = _albums.GetById("album-contrib-del");
            Assert.Single(updatedAlbum.Media);
            Assert.Equal("m1", updatedAlbum.Media[0].Id);
        }

        [Fact]
        public void UpdateContributors_RemovedContributor_ReturnsTheirMediaForDeletion()
        {
            var album = new Spokes_Server.Core.Models.Communication.Album 
            { 
                Id = "album-contrib-update", 
                OwnerId = "user-owner",
                ContributorUserIds = new List<string> { "user-contrib1", "user-contrib2" }
            };
            var m1 = new AlbumMedia { Id = "m1", AddedByUserId = "user-owner" };
            var m2 = new AlbumMedia { Id = "m2", AddedByUserId = "user-contrib1" };
            var m3 = new AlbumMedia { Id = "m3", AddedByUserId = "user-contrib2" };
            album.Media = new List<AlbumMedia> { m1, m2, m3 };
            _albums.Save(album);

            var removed = _albumService.UpdateContributors("album-contrib-update", "user-owner", new[] { "user-contrib2" }, null);
            
            Assert.Single(removed);
            Assert.Equal("m2", removed[0].Id); // contrib1 was removed
            
            var updatedAlbum = _albums.GetById("album-contrib-update");
            Assert.Equal(2, updatedAlbum.Media.Count);
            Assert.DoesNotContain(updatedAlbum.Media, m => m.Id == "m2");
            Assert.Single(updatedAlbum.ContributorUserIds);
            Assert.Equal("user-contrib2", updatedAlbum.ContributorUserIds[0]);
        }

        [Fact]
        public void UnshareAlbumWithChannel_RemovesChannel_WhenAuthorized()
        {
            var album = new Spokes_Server.Core.Models.Communication.Album
            {
                Id = "album-unshare",
                OwnerId = "user-owner",
                SharedWithChannelIds = new List<string> { "chan-1", "chan-2" }
            };
            _albums.Save(album);

            // Authorized call
            _albumService.UnshareAlbumWithChannel("album-unshare", "user-owner", "chan-1");

            var updated = _albums.GetById("album-unshare");
            Assert.Single(updated.SharedWithChannelIds);
            Assert.Equal("chan-2", updated.SharedWithChannelIds[0]);

            // Unauthorized call should be ignored
            _albumService.UnshareAlbumWithChannel("album-unshare", "user-unauthorized", "chan-2");
            var unchanged = _albums.GetById("album-unshare");
            Assert.Single(unchanged.SharedWithChannelIds);
            Assert.Equal("chan-2", unchanged.SharedWithChannelIds[0]);
        }

        [Fact]
        public void AlbumService_Mutations_TriggerOnAlbumUpdatedEvent()
        {
            var album = new Spokes_Server.Core.Models.Communication.Album
            {
                Id = "album-event-test",
                OwnerId = "user-owner"
            };
            _albums.Save(album);

            string? triggeredAlbumId = null;
            _albumService.OnAlbumUpdated += id => triggeredAlbumId = id;

            _channels.Save(new Spokes_Server.Core.Models.Communication.ChatChannel { Id = "chan-1" });
            _albumService.ShareAlbumWithChannel("album-event-test", "user-owner", "chan-1", null);
            Assert.Equal("album-event-test", triggeredAlbumId);

            triggeredAlbumId = null;
            _albumService.UnshareAlbumWithChannel("album-event-test", "user-owner", "chan-1");
            Assert.Equal("album-event-test", triggeredAlbumId);
        }
    }
}
