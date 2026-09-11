using Xunit;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Tests.Core.Services.Licensing
{
    public class VersionMetadataTests
    {
        [Theory]
        [InlineData("server-v2026.9.16-security", "server", "2026.9.16", true)]
        [InlineData("server-v2026.9.16", "server", "2026.9.16", false)]
        [InlineData("test-v2026.9.16-security", "test", "2026.9.16", true)]
        [InlineData("beta-v2026.9.16", "beta", "2026.9.16", false)]
        [InlineData("beta-v2026.9.16-security", "beta", "2026.9.16", true)]
        [InlineData("dev", "unknown", "dev", false)]
        [InlineData("", "unknown", "0.0.0", false)]
        [InlineData("server-v2026.12.1-security", "server", "2026.12.1", true)]
        [InlineData("server-v2026.1.0+build123", "server", "2026.1.0", false)]
        public void ParsesVersionStringCorrectly(string raw, string expectedChannel, string expectedClean, bool expectedSecurity)
        {
            var metadata = new VersionMetadata(raw);

            Assert.Equal(raw, metadata.RawVersion);
            Assert.Equal(expectedChannel, metadata.Channel);
            Assert.Equal(expectedClean, metadata.CleanVersion);
            Assert.Equal(expectedSecurity, metadata.IsSecurityPatch);
        }
    }
}
