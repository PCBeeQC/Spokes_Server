using Spokes_Server.Core.Constants;
using Xunit;

namespace Spokes_Server.Tests.Core.Constants;

public class SpokesConstantsTests
{
    [Fact]
    public void Constants_HaveExpectedDefaultValues()
    {
        Assert.Equal("https://push.spokes.sh/api/relay-push", SpokesConstants.PushRelayUrl);
        Assert.Equal("https://push.spokes.sh/api/relay-telemetry", SpokesConstants.TelemetryRelayUrl);
        Assert.NotNull(SpokesConstants.LicensePublicKeyPem);
        Assert.StartsWith("-----BEGIN RSA PUBLIC KEY-----", SpokesConstants.LicensePublicKeyPem);
        Assert.EndsWith("-----END RSA PUBLIC KEY-----", SpokesConstants.LicensePublicKeyPem);
    }
}
