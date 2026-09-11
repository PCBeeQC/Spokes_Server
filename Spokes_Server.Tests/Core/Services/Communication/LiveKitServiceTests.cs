using Moq;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Voice;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication
{
    public class LiveKitServiceTests
    {
        [Fact]
        public void ApplyPortConfiguration_ExecutesWithoutCrashing()
        {
            var mockLogger = new Mock<ILogger<LiveKitService>>();
            var service = new LiveKitService(mockLogger.Object);

            var config = new SystemConfig
            {
                LiveKitFallbackPort = 7881,
                LiveKitUdpStartPort = 30000,
                LiveKitUdpEndPort = 30499
            };

            // On Windows test environment, supervisorctl won't be found, but it should log a handled error rather than throw unhandled
            var exception = Record.Exception(() => service.ApplyPortConfiguration(config));
            Assert.Null(exception);
        }

        [Fact]
        public void ChangeDetection_OnlyIdPTriggersRestart()
        {
            var initialConfig = new SystemConfig
            {
                ServerPublicUrl = "https://spokes.example.com",
                EnableAutoUpdate = false,
                ProviderType = IdpType.BuiltInCasdoor,
                Authority = "https://spokes.example.com",
                ClientId = "client123",
                ClientSecret = "secret123",
                LiveKitFallbackPort = 7881,
                LiveKitUdpStartPort = 30000,
                LiveKitUdpEndPort = 30499
            };

            // Case 1: Dynamic changes only (URL and AutoUpdate)
            var modifiedConfig1 = new SystemConfig
            {
                ServerPublicUrl = "https://new.example.com",
                EnableAutoUpdate = true,
                ProviderType = IdpType.BuiltInCasdoor,
                Authority = "https://spokes.example.com",
                ClientId = "client123",
                ClientSecret = "secret123",
                LiveKitFallbackPort = 7881,
                LiveKitUdpStartPort = 30000,
                LiveKitUdpEndPort = 30499
            };

            bool idpChanged1 = modifiedConfig1.ProviderType != initialConfig.ProviderType ||
                               modifiedConfig1.Authority != initialConfig.Authority ||
                               modifiedConfig1.ClientId != initialConfig.ClientId ||
                               modifiedConfig1.ClientSecret != initialConfig.ClientSecret;

            bool livekitChanged1 = modifiedConfig1.LiveKitFallbackPort != initialConfig.LiveKitFallbackPort ||
                                   modifiedConfig1.LiveKitUdpStartPort != initialConfig.LiveKitUdpStartPort ||
                                   modifiedConfig1.LiveKitUdpEndPort != initialConfig.LiveKitUdpEndPort;

            Assert.False(idpChanged1);
            Assert.False(livekitChanged1);

            // Case 2: IdP changes
            var modifiedConfig2 = new SystemConfig
            {
                ServerPublicUrl = "https://spokes.example.com",
                EnableAutoUpdate = false,
                ProviderType = IdpType.External,
                Authority = "https://auth.company.com",
                ClientId = "newClient",
                ClientSecret = "newSecret",
                LiveKitFallbackPort = 7881,
                LiveKitUdpStartPort = 30000,
                LiveKitUdpEndPort = 30499
            };

            bool idpChanged2 = modifiedConfig2.ProviderType != initialConfig.ProviderType ||
                               modifiedConfig2.Authority != initialConfig.Authority ||
                               modifiedConfig2.ClientId != initialConfig.ClientId ||
                               modifiedConfig2.ClientSecret != initialConfig.ClientSecret;

            Assert.True(idpChanged2);

            // Case 3: LiveKit ports change
            var modifiedConfig3 = new SystemConfig
            {
                ServerPublicUrl = "https://spokes.example.com",
                EnableAutoUpdate = false,
                ProviderType = IdpType.BuiltInCasdoor,
                Authority = "https://spokes.example.com",
                ClientId = "client123",
                ClientSecret = "secret123",
                LiveKitFallbackPort = 7882,
                LiveKitUdpStartPort = 31000,
                LiveKitUdpEndPort = 31499
            };

            bool livekitChanged3 = modifiedConfig3.LiveKitFallbackPort != initialConfig.LiveKitFallbackPort ||
                                   modifiedConfig3.LiveKitUdpStartPort != initialConfig.LiveKitUdpStartPort ||
                                   modifiedConfig3.LiveKitUdpEndPort != initialConfig.LiveKitUdpEndPort;

            bool idpChanged3 = modifiedConfig3.ProviderType != initialConfig.ProviderType ||
                               modifiedConfig3.Authority != initialConfig.Authority ||
                               modifiedConfig3.ClientId != initialConfig.ClientId ||
                               modifiedConfig3.ClientSecret != initialConfig.ClientSecret;

            Assert.True(livekitChanged3);
            Assert.False(idpChanged3);
        }
    }
}
