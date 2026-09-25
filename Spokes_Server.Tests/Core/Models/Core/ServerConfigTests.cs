namespace Spokes_Server.Tests.Core.Models.Core;

using Spokes_Server.Core.Models.Core;

public class ServerConfigTests
{
    [Fact]
    public void ServerConfig_Defaults_AreSetCorrectly()
    {
        var before = DateTime.UtcNow;
        var config = new ServerConfig();
        var after = DateTime.UtcNow;

        Assert.Equal("Global", config.Id);
        Assert.Equal(string.Empty, config.ServerMasterRsaPublicKey);
        Assert.Equal(string.Empty, config.ServerMasterEncryptedPrivateKey);
        Assert.False(config.IsEscrowEnabled);
        Assert.Equal(string.Empty, config.DatabaseCreationVersion);
        Assert.Equal(string.Empty, config.DatabaseCreationSignature);
        Assert.Equal(string.Empty, config.DatabaseCreationId);
        Assert.Equal(string.Empty, config.DatabaseCreationIdSignature);
        Assert.InRange(config.LastUpdated, before, after);
    }

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
    public void ServerConfig_Properties_CanBeSetAndRead()
    {
        var updatedTime = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var config = new ServerConfig
        {
            Id = "CustomConfigId",
            ServerMasterRsaPublicKey = "publicKey123",
            ServerMasterEncryptedPrivateKey = "encryptedPrivateKey456",
            DatabaseCreationVersion = "1.0.0",
            DatabaseCreationSignature = "sig-creation",
            DatabaseCreationId = "db-id-789",
            DatabaseCreationIdSignature = "sig-db-id",
            LastUpdated = updatedTime
        };

        Assert.Equal("CustomConfigId", config.Id);
        Assert.Equal("publicKey123", config.ServerMasterRsaPublicKey);
        Assert.Equal("encryptedPrivateKey456", config.ServerMasterEncryptedPrivateKey);
        Assert.True(config.IsEscrowEnabled);
        Assert.Equal("1.0.0", config.DatabaseCreationVersion);
        Assert.Equal("sig-creation", config.DatabaseCreationSignature);
        Assert.Equal("db-id-789", config.DatabaseCreationId);
        Assert.Equal("sig-db-id", config.DatabaseCreationIdSignature);
        Assert.Equal(updatedTime, config.LastUpdated);
    }
}
