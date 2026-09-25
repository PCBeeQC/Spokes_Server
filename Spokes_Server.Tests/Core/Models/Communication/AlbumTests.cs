using System;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class AlbumTests
{
        [Fact]
        public void Album_Defaults_AreSetCorrectly()
        {
            var before = DateTime.UtcNow;
            var album = new Album();
            var after = DateTime.UtcNow;

            Assert.False(string.IsNullOrWhiteSpace(album.Id));
            Assert.True(Guid.TryParse(album.Id, out _));
            Assert.Equal(string.Empty, album.Title);
            Assert.Equal(string.Empty, album.Description);
            Assert.Equal(string.Empty, album.OwnerId);
            Assert.NotNull(album.SharedWithChannelIds);
            Assert.Empty(album.SharedWithChannelIds);
            Assert.NotNull(album.ContributorUserIds);
            Assert.Empty(album.ContributorUserIds);
            Assert.True(album.CreatedAt >= before && album.CreatedAt <= after);
            Assert.True(album.IsEncrypted);
            Assert.NotNull(album.EncryptedAlbumKeys);
            Assert.Empty(album.EncryptedAlbumKeys);
            Assert.NotNull(album.Media);
            Assert.Empty(album.Media);
        }

        [Fact]
        public void Album_Equals_ReturnsTrueForReferenceEquality()
        {
            var album = new Album();
            Assert.True(album.Equals(album));
        }

        [Fact]
        public void Album_Equals_ReturnsTrueForSameId()
        {
            var id = Guid.NewGuid().ToString();
            var album1 = new Album { Id = id, Title = "Album 1" };
            var album2 = new Album { Id = id, Title = "Album 2" };

            Assert.True(album1.Equals(album2));
            Assert.Equal(album1.GetHashCode(), album2.GetHashCode());
        }

        [Fact]
        public void Album_Equals_ReturnsFalseForDifferentId()
        {
            var album1 = new Album { Id = "id-1" };
            var album2 = new Album { Id = "id-2" };

            Assert.False(album1.Equals(album2));
        }

        [Fact]
        public void Album_Equals_ReturnsFalseForNull()
        {
            var album = new Album();
            Assert.False(album.Equals(null));
        }

        [Fact]
        public void Album_Equals_ReturnsFalseForDifferentType()
        {
            var album = new Album();
            var notAnAlbum = new object();

            Assert.False(album.Equals(notAnAlbum));
            Assert.False(album.Equals("some string"));
        }

        [Fact]
        public void Album_GetHashCode_WhenIdIsNull_DoesNotThrow()
        {
            var album = new Album { Id = null! };
            var hashCode = album.GetHashCode();
            Assert.IsType<int>(hashCode);
        }

        [Fact]
        public void AlbumMedia_Defaults_AreSetCorrectly()
        {
            var before = DateTime.UtcNow;
            var media = new AlbumMedia();
            var after = DateTime.UtcNow;

            Assert.False(string.IsNullOrWhiteSpace(media.Id));
            Assert.True(Guid.TryParse(media.Id, out _));
            Assert.Equal(string.Empty, media.FileName);
            Assert.Equal(string.Empty, media.FilePath);
            Assert.Equal(string.Empty, media.ContentType);
            Assert.Equal(0, media.FileSizeBytes);
            Assert.True(media.AddedAt >= before && media.AddedAt <= after);
            Assert.Equal(string.Empty, media.AddedByUserId);
            Assert.False(media.HasServerThumbnail);
            Assert.Null(media.ImageWidth);
            Assert.Null(media.ImageHeight);
        }

        [Fact]
        public void AlbumMedia_ThumbnailUrl_WhenHasServerThumbnailIsFalse_ReturnsFilePath()
        {
            var media = new AlbumMedia
            {
                HasServerThumbnail = false,
                FilePath = "/spokesapi/files/abc.jpg"
            };

            Assert.Equal("/spokesapi/files/abc.jpg", media.ThumbnailUrl);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AlbumMedia_ThumbnailUrl_WhenFilePathIsNullOrWhiteSpace_ReturnsFilePath(string? path)
        {
            var media = new AlbumMedia
            {
                HasServerThumbnail = true,
                FilePath = path!
            };

            Assert.Equal(path, media.ThumbnailUrl);
        }

        [Fact]
        public void AlbumMedia_ThumbnailUrl_WhenHasServerThumbnailIsTrueAndFilePathStartsWithSpokesapiFiles_ReturnsThumbPath()
        {
            var media = new AlbumMedia
            {
                HasServerThumbnail = true,
                FilePath = "/spokesapi/files/abc.jpg"
            };

            Assert.Equal("/spokesapi/files/thumb/abc.jpg", media.ThumbnailUrl);
        }

        [Fact]
        public void AlbumMedia_ThumbnailUrl_WhenHasServerThumbnailIsTrueAndFilePathDoesNotStartWithSpokesapiFiles_ReturnsFilePath()
        {
            var media = new AlbumMedia
            {
                HasServerThumbnail = true,
                FilePath = "/custom/path/abc.jpg"
            };

            Assert.Equal("/custom/path/abc.jpg", media.ThumbnailUrl);
        }
    }
