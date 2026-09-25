using Spokes_Server.Core.Constants;

namespace Spokes_Server.Tests.Core.Constants
{
    public class SpokesConstantsTests
    {
        [Fact]
        public void Constants_HaveExpectedDefaultValues()
        {
            Assert.Equal("https://push.spokes.sh/api/relay-push", SpokesConstants.PushRelayUrl);
            Assert.NotNull(SpokesConstants.LicensePublicKeyPem);
            Assert.StartsWith("-----BEGIN RSA PUBLIC KEY-----", SpokesConstants.LicensePublicKeyPem);
            Assert.EndsWith("-----END RSA PUBLIC KEY-----", SpokesConstants.LicensePublicKeyPem);
        }

        [Fact]
        public void LicenseHMACSalt_ReturnsDefaultOrEnvValue()
        {
            var salt = SpokesConstants.LicenseHMACSalt;
            Assert.False(string.IsNullOrWhiteSpace(salt));
        }

        [Fact]
        public void PushRelayKey_And_DefaultKlipyApiKey_ReturnString()
        {
            Assert.NotNull(SpokesConstants.PushRelayKey);
            Assert.NotNull(SpokesConstants.DefaultKlipyApiKey);
        }

        [Fact]
        public void EnvironmentVariableOverrides_WorkAsExpected()
        {
            var originalRelayKey = Environment.GetEnvironmentVariable("SPOKES_RELAY_KEY");
            var originalKlipyKey = Environment.GetEnvironmentVariable("SPOKES_KLIPY_KEY");
            var originalHmacSalt = Environment.GetEnvironmentVariable("SPOKES_HMAC_SALT");

            try
            {
                // Test relay key override
                Environment.SetEnvironmentVariable("SPOKES_RELAY_KEY", "test-relay-key-123");
                Assert.Equal("test-relay-key-123", SpokesConstants.PushRelayKey);

                Environment.SetEnvironmentVariable("SPOKES_RELAY_KEY", null);
                Assert.Equal("", SpokesConstants.PushRelayKey);

                // Test klipy key override
                Environment.SetEnvironmentVariable("SPOKES_KLIPY_KEY", "test-klipy-key-456");
                Assert.Equal("test-klipy-key-456", SpokesConstants.DefaultKlipyApiKey);

                Environment.SetEnvironmentVariable("SPOKES_KLIPY_KEY", null);
                Assert.Equal("", SpokesConstants.DefaultKlipyApiKey);

                // Test HMAC salt override
                Environment.SetEnvironmentVariable("SPOKES_HMAC_SALT", "custom-hmac-salt");
                Assert.Equal("custom-hmac-salt", SpokesConstants.LicenseHMACSalt);

                Environment.SetEnvironmentVariable("SPOKES_HMAC_SALT", null);
                Assert.Equal("default-development-salt", SpokesConstants.LicenseHMACSalt);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SPOKES_RELAY_KEY", originalRelayKey);
                Environment.SetEnvironmentVariable("SPOKES_KLIPY_KEY", originalKlipyKey);
                Environment.SetEnvironmentVariable("SPOKES_HMAC_SALT", originalHmacSalt);
            }
        }
    }
}
