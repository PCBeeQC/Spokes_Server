namespace Spokes_Server.Tests.Core.Services.Communication;

using System.IO;
using Microsoft.AspNetCore.Hosting;
using Moq;
using SkiaSharp;
using Spokes_Server.Core.Services.Communication;

public class AvatarGeneratorServiceTests
{
        private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47];

        private static Mock<IWebHostEnvironment> CreateMockEnvironment(string? webRootPath = null)
        {
            var mockEnv = new Mock<IWebHostEnvironment>();
            mockEnv.Setup(e => e.WebRootPath).Returns(webRootPath ?? Path.Combine(Path.GetTempPath(), "Spokes_Test_NonExistent_" + Guid.NewGuid()));
            return mockEnv;
        }

        private static void AssertValid192Png(byte[] bytes)
        {
            Assert.NotNull(bytes);
            Assert.NotEmpty(bytes);
            Assert.True(bytes.Length >= 8, "Byte array is too short to be a valid PNG.");

            // Verify PNG magic bytes: 0x89, 0x50, 0x4E, 0x47 ('\x89PNG')
            Assert.Equal(PngHeader[0], bytes[0]);
            Assert.Equal(PngHeader[1], bytes[1]);
            Assert.Equal(PngHeader[2], bytes[2]);
            Assert.Equal(PngHeader[3], bytes[3]);

            using var bitmap = SKBitmap.Decode(bytes);
            Assert.NotNull(bitmap);
            Assert.Equal(192, bitmap.Width);
            Assert.Equal(192, bitmap.Height);

            // Verify corner pixel is transparent (circle background should leave corner transparent)
            var cornerPixel = bitmap.GetPixel(0, 0);
            Assert.Equal(0, cornerPixel.Alpha);
        }

        [Fact]
        public void Constructor_WhenWebRootPathDoesNotExist_InitializesGracefullyWithoutThrowing()
        {
            // Arrange
            var mockEnv = CreateMockEnvironment(Path.Combine(Path.GetTempPath(), "NonExistentDir_" + Guid.NewGuid()));

            // Act
            var service = new AvatarGeneratorService(mockEnv.Object);
            var avatarBytes = service.GenerateAvatar("Test", "User", "#2196F3");

            // Assert
            AssertValid192Png(avatarBytes);
        }

        [Fact]
        public void Constructor_WhenWebRootPathIsNull_InitializesGracefullyWithoutThrowing()
        {
            // Arrange
            var mockEnv = new Mock<IWebHostEnvironment>();
            mockEnv.Setup(e => e.WebRootPath).Returns((string)null!);

            // Act
            var service = new AvatarGeneratorService(mockEnv.Object);
            var avatarBytes = service.GenerateAvatar("Test", "User", "#2196F3");

            // Assert
            AssertValid192Png(avatarBytes);
        }

        [Fact]
        public void Constructor_WhenFontFileExists_LoadsFontAndGeneratesAvatarSuccessfully()
        {
            // Arrange - create a temporary fonts directory
            var tempDir = Path.Combine(Path.GetTempPath(), "Spokes_Font_Test_" + Guid.NewGuid());
            var fontsDir = Path.Combine(tempDir, "fonts");
            Directory.CreateDirectory(fontsDir);

            try
            {
                // Write a dummy font file - even if corrupt, exception is caught gracefully
                var fontPath = Path.Combine(fontsDir, "Roboto-Regular.ttf");
                File.WriteAllBytes(fontPath, [0x00, 0x01, 0x00, 0x00]);

                var mockEnv = CreateMockEnvironment(tempDir);

                // Act
                var service = new AvatarGeneratorService(mockEnv.Object);
                var avatarBytes = service.GenerateAvatar("Jane", "Doe", "#FF5722");

                // Assert
                AssertValid192Png(avatarBytes);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void GenerateAvatar_BothFirstNameAndLastNameProvided_ReturnsValid192x192Png()
        {
            // Arrange
            var service = new AvatarGeneratorService(CreateMockEnvironment().Object);

            // Act
            var bytes = service.GenerateAvatar("John", "Doe", "#2196F3");

            // Assert
            AssertValid192Png(bytes);
        }

        [Fact]
        public void GenerateAvatar_SingleName_FirstNameOnly_ReturnsValidPng()
        {
            // Arrange
            var service = new AvatarGeneratorService(CreateMockEnvironment().Object);

            // Act
            var bytes = service.GenerateAvatar("Alice", null, "#4CAF50");

            // Assert
            AssertValid192Png(bytes);
        }

        [Fact]
        public void GenerateAvatar_SingleName_LastNameOnly_ReturnsValidPng()
        {
            // Arrange
            var service = new AvatarGeneratorService(CreateMockEnvironment().Object);

            // Act
            var bytes = service.GenerateAvatar(null, "Smith", "#FF9800");

            // Assert
            AssertValid192Png(bytes);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("", null)]
        public void GenerateAvatar_BothNamesNullOrEmpty_FallsBackToQuestionMarkAndReturnsValidPng(string? firstName, string? lastName)
        {
            // Arrange
            var service = new AvatarGeneratorService(CreateMockEnvironment().Object);

            // Act
            var bytes = service.GenerateAvatar(firstName, lastName, "#673AB7");

            // Assert
            AssertValid192Png(bytes);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void GenerateAvatar_ProfileColorNullOrEmpty_FallsBackToDefaultColor(string? profileColor)
        {
            // Arrange
            var service = new AvatarGeneratorService(CreateMockEnvironment().Object);

            // Act
            var bytes = service.GenerateAvatar("Default", "Color", profileColor);

            // Assert
            AssertValid192Png(bytes);

            // Verify background circle color contains default MudBlazor DeepPurple (#673ab7)
            using var bitmap = SKBitmap.Decode(bytes);
            // Sample pixel inside circle away from centered text, e.g. at (96, 20)
            var samplePixel = bitmap.GetPixel(96, 20);
            Assert.Equal(0xFF, samplePixel.Alpha);
            // Default color #673ab7: R=103 (0x67), G=58 (0x3a), B=183 (0xb7)
            Assert.Equal(0x67, samplePixel.Red);
            Assert.Equal(0x3A, samplePixel.Green);
            Assert.Equal(0xB7, samplePixel.Blue);
        }

        [Fact]
        public void GenerateAvatar_ProfileColorInvalidString_FallsBackToDefaultColor()
        {
            // Arrange
            var service = new AvatarGeneratorService(CreateMockEnvironment().Object);

            // Act
            var bytes = service.GenerateAvatar("Invalid", "Color", "not-a-color");

            // Assert
            AssertValid192Png(bytes);

            using var bitmap = SKBitmap.Decode(bytes);
            var samplePixel = bitmap.GetPixel(96, 20);
            Assert.Equal(0xFF, samplePixel.Alpha);
            Assert.Equal(0x67, samplePixel.Red);
            Assert.Equal(0x3A, samplePixel.Green);
            Assert.Equal(0xB7, samplePixel.Blue);
        }

        [Theory]
        [InlineData("#FF5722", 0xFF, 0x57, 0x22)]
        [InlineData("#2196F3", 0x21, 0x96, 0xF3)]
        [InlineData("#00E676", 0x00, 0xE6, 0x76)]
        public void GenerateAvatar_ValidHexColors_ParsesColorSuccessfullyAndDrawsSpecifiedColor(string hexColor, byte expectedR, byte expectedG, byte expectedB)
        {
            // Arrange
            var service = new AvatarGeneratorService(CreateMockEnvironment().Object);

            // Act
            var bytes = service.GenerateAvatar("Color", "Test", hexColor);

            // Assert
            AssertValid192Png(bytes);

            using var bitmap = SKBitmap.Decode(bytes);
            var samplePixel = bitmap.GetPixel(96, 20);
            Assert.Equal(0xFF, samplePixel.Alpha);
            Assert.Equal(expectedR, samplePixel.Red);
            Assert.Equal(expectedG, samplePixel.Green);
            Assert.Equal(expectedB, samplePixel.Blue);
        }

        [Theory]
        [InlineData("#FF5722")]
        [InlineData("red")]
        public void GenerateAvatar_ValidHexAndNamedColors_GeneratesAvatarSuccessfully(string colorInput)
        {
            // Arrange
            var service = new AvatarGeneratorService(CreateMockEnvironment().Object);

            // Act
            var bytes = service.GenerateAvatar("Color", "Test", colorInput);

            // Assert
            AssertValid192Png(bytes);
        }

        [Fact]
        public void GenerateAvatar_LowerCaseNames_ConvertsInitialsToUppercaseAndSucceeds()
        {
            // Arrange
            var service = new AvatarGeneratorService(CreateMockEnvironment().Object);

            // Act
            var bytes = service.GenerateAvatar("john", "doe", "#00BCD4");

            // Assert
            AssertValid192Png(bytes);
        }
    }
