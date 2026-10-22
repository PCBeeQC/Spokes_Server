using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Licensing;

public class RelayTrialTicketServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly RelayTrialTicketService _sut;

    public RelayTrialTicketServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_RelayTrialTicket_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _testDataDir }).Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _serverConfigs = new ServerConfigRepository(_persistence, config);
        _sut = new RelayTrialTicketService(_serverConfigs);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        try { Directory.Delete(_testDataDir, true); } catch (IOException) { }
    }

    private static string NewPublicKeyXml()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ToXmlString(false);
    }

    private ServerConfig SaveConfigWithKey(string keyXml)
    {
        var config = _serverConfigs.GetOrCreateGlobalConfig();
        config.ServerMasterRsaPublicKey = keyXml;
        _serverConfigs.Save(config);
        return config;
    }

    private static RelayTrialTicket TicketFor(string keyXml, string expiresAt = "2026-12-05T10:00:00Z") => new()
    {
        Fingerprint = RelayTrialTicketService.ComputeFingerprint(keyXml)!,
        IssuedAt = "2026-10-05T10:00:00Z",
        ExpiresAt = expiresAt,
        Signature = "relay-sig"
    };

    [Fact]
    public void ComputeFingerprint_IsBase64UrlSha256OfSubjectPublicKeyInfo()
    {
        using var rsa = RSA.Create(2048);
        var expected = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

        var fingerprint = RelayTrialTicketService.ComputeFingerprint(rsa.ToXmlString(false));

        Assert.Equal(expected, fingerprint);
        Assert.Equal(43, fingerprint!.Length);
    }

    [Fact]
    public void ComputeFingerprint_SameKey_IsStable_DifferentKeys_Differ()
    {
        var keyA = NewPublicKeyXml();
        var keyB = NewPublicKeyXml();

        Assert.Equal(RelayTrialTicketService.ComputeFingerprint(keyA), RelayTrialTicketService.ComputeFingerprint(keyA));
        Assert.NotEqual(RelayTrialTicketService.ComputeFingerprint(keyA), RelayTrialTicketService.ComputeFingerprint(keyB));
    }

    [Fact]
    public void ComputeFingerprint_IgnoresXmlFormatting()
    {
        var key = NewPublicKeyXml();
        var reformatted = key.Replace("><", ">\n  <");

        Assert.Equal(RelayTrialTicketService.ComputeFingerprint(key), RelayTrialTicketService.ComputeFingerprint(reformatted));
    }

    [Fact]
    public void ComputeFingerprint_IgnoresPrivateKeyMaterial()
    {
        // The fingerprint only depends on the public half, so it survives master-password changes (which only re-wrap the private key).
        using var rsa = RSA.Create(2048);

        Assert.Equal(RelayTrialTicketService.ComputeFingerprint(rsa.ToXmlString(false)), RelayTrialTicketService.ComputeFingerprint(rsa.ToXmlString(true)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<RSAKeyValue><Modulus>not-base64!</Modulus></RSAKeyValue>")]
    [InlineData("not xml at all")]
    public void ComputeFingerprint_MissingOrInvalidKey_ReturnsNull(string? keyXml)
    {
        Assert.Null(RelayTrialTicketService.ComputeFingerprint(keyXml));
    }

    [Fact]
    public void GetTicketForCurrentKey_ReturnsOnlyMatchingTicket()
    {
        var key = NewPublicKeyXml();
        var own = new ServerConfig { ServerMasterRsaPublicKey = key, RelayTrialTicket = TicketFor(key) };
        var foreign = new ServerConfig { ServerMasterRsaPublicKey = key, RelayTrialTicket = TicketFor(NewPublicKeyXml()) };
        var none = new ServerConfig { ServerMasterRsaPublicKey = key };

        Assert.Same(own.RelayTrialTicket, RelayTrialTicketService.GetTicketForCurrentKey(own));
        Assert.Null(RelayTrialTicketService.GetTicketForCurrentKey(foreign));
        Assert.Null(RelayTrialTicketService.GetTicketForCurrentKey(none));
    }

    [Fact]
    public void TryGetExpiry_ParsesUtcTimestamp()
    {
        Assert.True(RelayTrialTicketService.TryGetExpiry(new RelayTrialTicket { ExpiresAt = "2026-12-05T10:00:00Z" }, out var expiresAt));
        Assert.Equal(new DateTime(2026, 12, 5, 10, 0, 0, DateTimeKind.Utc), expiresAt);
        Assert.Equal(DateTimeKind.Utc, expiresAt.Kind);

        Assert.False(RelayTrialTicketService.TryGetExpiry(new RelayTrialTicket { ExpiresAt = "garbage" }, out _));
    }

    [Fact]
    public void StoreTicket_MatchingFingerprint_PersistsImmediately()
    {
        var key = NewPublicKeyXml();
        SaveConfigWithKey(key);
        var ticket = TicketFor(key);

        _sut.StoreTicket(ticket);
        _persistence.FlushAll();

        var reloaded = new ServerConfigRepository(_persistence, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _testDataDir }).Build()).GetOrCreateGlobalConfig();
        Assert.NotNull(reloaded.RelayTrialTicket);
        Assert.Equal(ticket.Fingerprint, reloaded.RelayTrialTicket!.Fingerprint);
        Assert.Equal(ticket.ExpiresAt, reloaded.RelayTrialTicket.ExpiresAt);
    }

    [Fact]
    public void StoreTicket_ForeignFingerprint_IsIgnored()
    {
        SaveConfigWithKey(NewPublicKeyXml());

        _sut.StoreTicket(TicketFor(NewPublicKeyXml()));

        Assert.Null(_serverConfigs.GetOrCreateGlobalConfig().RelayTrialTicket);
    }

    [Fact]
    public void StoreTicket_IdenticalTicket_DoesNotRewriteConfig()
    {
        var key = NewPublicKeyXml();
        SaveConfigWithKey(key);
        _sut.StoreTicket(TicketFor(key));
        var stampAfterFirstStore = _serverConfigs.GetOrCreateGlobalConfig().LastUpdated;

        _sut.StoreTicket(TicketFor(key));

        Assert.Equal(stampAfterFirstStore, _serverConfigs.GetOrCreateGlobalConfig().LastUpdated);
    }

    [Fact]
    public void StoreTicket_NewerTicket_ReplacesStoredTicket()
    {
        var key = NewPublicKeyXml();
        SaveConfigWithKey(key);
        _sut.StoreTicket(TicketFor(key, "2026-12-05T10:00:00Z"));

        _sut.StoreTicket(TicketFor(key, "2027-01-05T10:00:00Z"));

        Assert.Equal("2027-01-05T10:00:00Z", _serverConfigs.GetOrCreateGlobalConfig().RelayTrialTicket?.ExpiresAt);
    }
}
