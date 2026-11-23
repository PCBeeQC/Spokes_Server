using System;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class AlbumTests
{
        [Fact]
        public void Album_Equals_And_GetHashCode_Behavior()
        {
            var id = Guid.NewGuid().ToString();
            var album1 = new Album { Id = id, Title = "Album 1" };
            var album2 = new Album { Id = id, Title = "Album 2" };
            var album3 = new Album { Id = "id-2" };
            var nullIdAlbum = new Album { Id = null! };

            Assert.True(album1.Equals(album1));
            Assert.True(album1.Equals(album2));
            Assert.Equal(album1.GetHashCode(), album2.GetHashCode());
            Assert.False(album1.Equals(album3));
            Assert.False(album1.Equals(null));
            Assert.False(album1.Equals(new object()));
            Assert.False(album1.Equals("some string"));
            Assert.IsType<int>(nullIdAlbum.GetHashCode());
        }

        [Fact]
        public void AlbumMedia_ThumbnailUrl_WhenHasServerThumbnailIsFalse_SelfHealableImageUsesThumb_NonImageReturnsFilePath()
        {
            var imageMedia = new AlbumMedia
            {
                HasServerThumbnail = false,
                FilePath = "/spokesapi/files/abc.jpg"
            };

            Assert.True(imageMedia.CanUseServerThumbnail);
            Assert.Equal("/spokesapi/files/thumb/abc.jpg", imageMedia.ThumbnailUrl);

            var videoMedia = new AlbumMedia
            {
                HasServerThumbnail = false,
                FilePath = "/spokesapi/files/abc.mp4"
            };

            Assert.False(videoMedia.CanUseServerThumbnail);
            Assert.Equal("/spokesapi/files/abc.mp4", videoMedia.ThumbnailUrl);
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
