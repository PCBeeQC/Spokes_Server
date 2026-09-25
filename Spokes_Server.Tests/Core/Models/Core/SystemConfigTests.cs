using System.Text.Json;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Tests.Core.Models.Core;

public class SystemConfigTests
{
    [Fact]
    public void IdpType_EnumValues_MatchExpectedUnderlyingInts()
    {
        Assert.Equal(0, (int)IdpType.None);
        Assert.Equal(1, (int)IdpType.BuiltInCasdoor);
        Assert.Equal(2, (int)IdpType.External);
    }

    [Theory]
    [InlineData("None", IdpType.None)]
    [InlineData("BuiltInCasdoor", IdpType.BuiltInCasdoor)]
    [InlineData("External", IdpType.External)]
    public void IdpType_StringParsing_ParsesCorrectly(string value, IdpType expected)
    {
        var parsed = Enum.Parse<IdpType>(value);
        Assert.Equal(expected, parsed);

        Assert.True(Enum.TryParse<IdpType>(value, out var tryParsed));
        Assert.Equal(expected, tryParsed);
    }

    [Fact]
    public void SystemConfig_Initialization_SetsDefaultsCorrectly()
    {
        var config = new SystemConfig();

        Assert.IsAssignableFrom<IDataEntity>(config);
        Assert.Equal("system_config", config.Id);
        Assert.False(config.IsSetupComplete);
        Assert.False(config.EulaAccepted);
        Assert.False(config.IsCasdoorSanitized);
        Assert.Equal(IdpType.None, config.ProviderType);

        Assert.Equal(string.Empty, config.ServerPublicUrl);
        Assert.False(string.IsNullOrWhiteSpace(config.PublicBrandingToken));
        Assert.Equal(32, config.PublicBrandingToken.Length);

        Assert.Equal("spokes_voice_key", config.LiveKitApiKey);
        Assert.False(string.IsNullOrWhiteSpace(config.LiveKitApiSecret));
        Assert.Equal(32, config.LiveKitApiSecret.Length);
        Assert.Equal(7881, config.LiveKitFallbackPort);
        Assert.Equal(30000, config.LiveKitUdpStartPort);
        Assert.Equal(30499, config.LiveKitUdpEndPort);

        Assert.Equal(string.Empty, config.Authority);
        Assert.Equal(string.Empty, config.ClientId);
        Assert.Equal(string.Empty, config.ClientSecret);
        Assert.False(config.EnableAutoUpdate);
    }

    [Fact]
    public void SystemConfig_GuidTokens_AreUniquePerInstance()
    {
        var config1 = new SystemConfig();
        var config2 = new SystemConfig();

        Assert.NotEqual(config1.PublicBrandingToken, config2.PublicBrandingToken);
        Assert.NotEqual(config1.LiveKitApiSecret, config2.LiveKitApiSecret);
    }

    [Fact]
    public void SystemConfig_PropertyMutations_WorkCorrectly()
    {
        var config = new SystemConfig
        {
            Id = "custom_config_id",
            IsSetupComplete = true,
            EulaAccepted = true,
            IsCasdoorSanitized = true,
            ProviderType = IdpType.External,
            ServerPublicUrl = "https://spokes.example.com",
            PublicBrandingToken = "custom_branding_token_123",
            LiveKitApiKey = "custom_livekit_key",
            LiveKitApiSecret = "custom_livekit_secret",
            LiveKitFallbackPort = 8080,
            LiveKitUdpStartPort = 40000,
            LiveKitUdpEndPort = 40500,
            Authority = "https://auth.example.com",
            ClientId = "client-id-xyz",
            ClientSecret = "client-secret-abc",
            EnableAutoUpdate = true
        };

        Assert.Equal("custom_config_id", config.Id);
        Assert.True(config.IsSetupComplete);
        Assert.True(config.EulaAccepted);
        Assert.True(config.IsCasdoorSanitized);
        Assert.Equal(IdpType.External, config.ProviderType);
        Assert.Equal("https://spokes.example.com", config.ServerPublicUrl);
        Assert.Equal("custom_branding_token_123", config.PublicBrandingToken);
        Assert.Equal("custom_livekit_key", config.LiveKitApiKey);
        Assert.Equal("custom_livekit_secret", config.LiveKitApiSecret);
        Assert.Equal(8080, config.LiveKitFallbackPort);
        Assert.Equal(40000, config.LiveKitUdpStartPort);
        Assert.Equal(40500, config.LiveKitUdpEndPort);
        Assert.Equal("https://auth.example.com", config.Authority);
        Assert.Equal("client-id-xyz", config.ClientId);
        Assert.Equal("client-secret-abc", config.ClientSecret);
        Assert.True(config.EnableAutoUpdate);
    }

    [Fact]
    public void SystemConfig_JsonSerialization_RoundTripsCorrectly()
    {
        var original = new SystemConfig
        {
            Id = "custom_id",
            IsSetupComplete = true,
            EulaAccepted = true,
            IsCasdoorSanitized = true,
            ProviderType = IdpType.BuiltInCasdoor,
            ServerPublicUrl = "https://spokes.example.com",
            PublicBrandingToken = "brand_123",
            LiveKitApiKey = "livekit_key",
            LiveKitApiSecret = "livekit_secret",
            LiveKitFallbackPort = 9000,
            LiveKitUdpStartPort = 50000,
            LiveKitUdpEndPort = 50500,
            Authority = "https://idp.example.com",
            ClientId = "cid",
            ClientSecret = "csecret",
            EnableAutoUpdate = true
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SystemConfig>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.IsSetupComplete, deserialized.IsSetupComplete);
        Assert.Equal(original.EulaAccepted, deserialized.EulaAccepted);
        Assert.Equal(original.IsCasdoorSanitized, deserialized.IsCasdoorSanitized);
        Assert.Equal(original.ProviderType, deserialized.ProviderType);
        Assert.Equal(original.ServerPublicUrl, deserialized.ServerPublicUrl);
        Assert.Equal(original.PublicBrandingToken, deserialized.PublicBrandingToken);
        Assert.Equal(original.LiveKitApiKey, deserialized.LiveKitApiKey);
        Assert.Equal(original.LiveKitApiSecret, deserialized.LiveKitApiSecret);
        Assert.Equal(original.LiveKitFallbackPort, deserialized.LiveKitFallbackPort);
        Assert.Equal(original.LiveKitUdpStartPort, deserialized.LiveKitUdpStartPort);
        Assert.Equal(original.LiveKitUdpEndPort, deserialized.LiveKitUdpEndPort);
        Assert.Equal(original.Authority, deserialized.Authority);
        Assert.Equal(original.ClientId, deserialized.ClientId);
        Assert.Equal(original.ClientSecret, deserialized.ClientSecret);
        Assert.Equal(original.EnableAutoUpdate, deserialized.EnableAutoUpdate);
    }
}
