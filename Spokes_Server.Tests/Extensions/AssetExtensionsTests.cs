using System.Collections.Generic;
using Spokes_Server.Extensions;
using Xunit;

namespace Spokes_Server.Tests.Extensions
{
    public class AssetExtensionsTests
    {
        [Fact]
        public void AppVersion_IsNotNullOrEmpty()
        {
            // Assert
            Assert.False(string.IsNullOrWhiteSpace(AssetExtensions.AppVersion));
        }

        [Fact]
        public void Bypassed_WithDynamicDictionaryIndexer_ResolvesUrlAndAppendsVersion()
        {
            // Arrange
            var assets = new Dictionary<string, string>
            {
                ["scripts/main.razor.js"] = "scripts/main.razor.fingerprinted.js"
            };
            var path = "scripts/main.razor.js";

            // Act
            var result = assets.Bypassed(path);

            // Assert
            Assert.Equal($"scripts/main.razor.fingerprinted.js?v={AssetExtensions.AppVersion}", result);
        }

        [Fact]
        public void Bypassed_WhenIndexerThrowsOrNotFound_UsesRawPathWithAppVersion()
        {
            // Arrange
            var plainObject = new object();
            var path = "scripts/plain.js";

            // Act
            var result = plainObject.Bypassed(path);

            // Assert
            Assert.Equal($"scripts/plain.js?v={AssetExtensions.AppVersion}", result);
        }

        [Fact]
        public void Bypassed_WhenKeyNotFoundInDictionary_UsesRawPathWithAppVersion()
        {
            // Arrange
            var assets = new Dictionary<string, string>();
            var path = "scripts/missing.js";

            // Act
            var result = assets.Bypassed(path);

            // Assert
            Assert.Equal($"scripts/missing.js?v={AssetExtensions.AppVersion}", result);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Bypassed_WhenResolvedUrlIsNullOrEmpty_UsesRawPathWithAppVersion(string? resolvedValue)
        {
            // Arrange
            var assets = new Dictionary<string, string?>
            {
                ["scripts/empty.js"] = resolvedValue
            };
            var path = "scripts/empty.js";

            // Act
            var result = assets.Bypassed(path);

            // Assert
            Assert.Equal($"scripts/empty.js?v={AssetExtensions.AppVersion}", result);
        }

        [Fact]
        public void Bypassed_WhenIndexerReturnsNonString_UsesRawPathWithAppVersion()
        {
            // Arrange
            var assets = new Dictionary<string, object>
            {
                ["scripts/number.js"] = 12345
            };
            var path = "scripts/number.js";

            // Act
            var result = assets.Bypassed(path);

            // Assert
            Assert.Equal($"scripts/number.js?v={AssetExtensions.AppVersion}", result);
        }
    }
}
