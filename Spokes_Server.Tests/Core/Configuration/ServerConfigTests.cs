namespace Spokes_Server.Tests.Core.Configuration;

using System.Text.Json;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Services.Licensing;

public class ServerConfigTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("rsa-public-key-sample", true)]
    public void ServerConfig_IsEscrowEnabled_CalculatesCorrectly(string? rsaPublicKey, bool expected)
    {
        var config = new ServerConfig
        {
            ServerMasterRsaPublicKey = rsaPublicKey!
        };

        Assert.Equal(expected, config.IsEscrowEnabled);
    }

    [Fact]
    public void ServerConfig_LegacyJsonWithRemovedSignatureFields_StillDeserializes()
    {
        // ServerConfig.json written by releases before relay trial tickets contains the removed HMAC signature fields.
        const string legacyJson = """
            {
              "Id": "Global",
              "DatabaseCreationVersion": "2026.9.5",
              "DatabaseCreationSignature": "old-hmac",
              "DatabaseCreationId": "db-id-1",
              "DatabaseCreationIdSignature": "old-id-hmac"
            }
            """;

        var config = JsonSerializer.Deserialize<ServerConfig>(legacyJson);

        Assert.NotNull(config);
        Assert.Equal("2026.9.5", config!.DatabaseCreationVersion);
        Assert.Equal("db-id-1", config.DatabaseCreationId);
        Assert.Null(config.RelayTrialTicket);
    }

    [Fact]
    public void ServerConfig_RelayTrialTicket_RoundTripsThroughJson()
    {
        var config = new ServerConfig
        {
            RelayTrialTicket = new RelayTrialTicket { Fingerprint = "fp", IssuedAt = "2026-10-01T00:00:00Z", ExpiresAt = "2026-12-01T00:00:00Z", Signature = "sig" }
        };

        var restored = JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(config));

        Assert.NotNull(restored?.RelayTrialTicket);
        Assert.Equal("fp", restored!.RelayTrialTicket!.Fingerprint);
        Assert.Equal("2026-10-01T00:00:00Z", restored.RelayTrialTicket.IssuedAt);
        Assert.Equal("2026-12-01T00:00:00Z", restored.RelayTrialTicket.ExpiresAt);
        Assert.Equal("sig", restored.RelayTrialTicket.Signature);
    }
}
