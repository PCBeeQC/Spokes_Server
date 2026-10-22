using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Xunit;

namespace Spokes_Server.Tests.Core.Configuration;

public class SpokesOptionsTests
{
    [Fact]
    public void SpokesOptions_Defaults_MatchExpectedConstants()
    {
        var options = new SpokesOptions();

        Assert.Equal(SpokesConstants.PushRelayUrl, options.PushRelayUrl);
        Assert.Equal(SpokesConstants.TelemetryRelayUrl, options.TelemetryRelayUrl);
        Assert.Equal(string.Empty, options.PushRelayKey);
        Assert.Equal(string.Empty, options.DefaultKlipyApiKey);
        Assert.Equal(SpokesConstants.LicensePublicKeyPem, options.LicensePublicKeyPem);
    }

    [Fact]
    public void SpokesOptions_BindsFromConfigurationSection()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["Spokes:PushRelayUrl"] = "https://custom.relay/api",
            ["Spokes:TelemetryRelayUrl"] = "https://custom.relay/api/telemetry",
            ["Spokes:PushRelayKey"] = "custom-relay-key",
            ["Spokes:DefaultKlipyApiKey"] = "custom-klipy-key"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSpokesConfiguration();

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<SpokesOptions>>().Value;

        Assert.Equal("https://custom.relay/api", options.PushRelayUrl);
        Assert.Equal("https://custom.relay/api/telemetry", options.TelemetryRelayUrl);
        Assert.Equal("custom-relay-key", options.PushRelayKey);
        Assert.Equal("custom-klipy-key", options.DefaultKlipyApiKey);
    }

    [Fact]
    public void SpokesOptions_FallsBackToEnvironmentVariableConfigurationKeys()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["SPOKES_RELAY_KEY"] = "env-relay-key",
            ["SPOKES_KLIPY_KEY"] = "env-klipy-key"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        var services = new ServiceCollection();
        services.AddSpokesConfiguration(configuration);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<SpokesOptions>>().Value;

        Assert.Equal("env-relay-key", options.PushRelayKey);
        Assert.Equal("env-klipy-key", options.DefaultKlipyApiKey);
    }
}
