using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Maintenance.Destinations;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class WebDavBackupProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly DiskPersistenceService _persistence;
    private readonly IConfiguration _config;
    private readonly CompanyProfileRepository _profiles;

    public WebDavBackupProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_WebDavTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _testDir })
            .Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _profiles = new CompanyProfileRepository(_persistence, _config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    private static IHttpClientFactory CreateMockHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage req, CancellationToken ct) => responder(req, ct));

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("WebDavBackup")).Returns(httpClient);
        return factoryMock.Object;
    }

    [Theory]
    [InlineData("https://192.168.1.50:5006/webdav/backups/", "/webdav/backups/Spokes_Backup_20260101_000000.spkbackup", true)]
    [InlineData("https://192.168.1.50:5006/webdav/backups/", "Spokes_Backup_20260101_000000.zip", true)]
    [InlineData("https://192.168.1.50:5006/webdav/backups/", "https://192.168.1.50:5006/webdav/backups/Spokes_Backup_20260101_000000.spkbackup", true)]
    [InlineData("http://10.0.0.12:5005/nas/spokes/", "/nas/spokes/Spokes_Backup_20260101_000000.spkbackup", true)]
    [InlineData("https://192.168.1.50:5006/webdav/backups/", "https://evil.tld/webdav/backups/Spokes_Backup_20260101_000000.spkbackup", false)]
    [InlineData("https://192.168.1.50:5006/webdav/backups/", "//evil.tld/Spokes_Backup_20260101_000000.spkbackup", false)]
    [InlineData("https://192.168.1.50:5006/webdav/backups/", "http://192.168.1.50:5006/webdav/backups/Spokes_Backup_20260101_000000.spkbackup", false)]
    [InlineData("https://192.168.1.50:5006/webdav/backups/", "https://192.168.1.50:8443/webdav/backups/Spokes_Backup_20260101_000000.spkbackup", false)]
    [InlineData("https://192.168.1.50:5006/webdav/backups/", "https://user:pass@192.168.1.50:5006/webdav/backups/Spokes_Backup_20260101_000000.spkbackup", false)]
    [InlineData("https://192.168.1.50:5006/webdav/backups/", "\\\\evil.tld\\share\\Spokes_Backup_20260101_000000.spkbackup", false)]
    public void TryResolveSameOriginUri_PreservesLanRfc1918OriginAndRejectsCrossOriginTargets(
        string configuredServerUrl,
        string candidateHref,
        bool expectedAllowed)
    {
        Assert.True(WebDavBackupProvider.TryParseConfiguredBaseUri(configuredServerUrl, out var baseUri));

        var allowed = WebDavBackupProvider.TryResolveSameOriginUri(baseUri, candidateHref, out var resolvedUri);
        Assert.Equal(expectedAllowed, allowed);

        if (expectedAllowed)
        {
            Assert.Equal(baseUri.Scheme, resolvedUri.Scheme);
            Assert.Equal(baseUri.Host, resolvedUri.Host);
            Assert.Equal(baseUri.Port, resolvedUri.Port);
            Assert.Equal(string.Empty, resolvedUri.UserInfo);
        }
    }

    [Fact]
    public async Task ListBackupsAsync_OnLanNas_KeepsSameOriginItems_AndSkipsCrossOriginHrefsAndTmpZips()
    {
        var profile = _profiles.Get();
        profile.Backup.WebDav.IsEnabled = true;
        profile.Backup.WebDav.ServerUrl = "http://192.168.1.50:5005/backups";
        profile.Backup.WebDav.Username = "nas-admin";
        profile.Backup.WebDav.Password = "SecretNasPassword!";
        _profiles.Save(profile);

        const string propfindXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/backups/Spokes_Backup_20260501_010000.spkbackup</d:href>
                <d:propstat><d:prop><d:getcontentlength>1024</d:getcontentlength><d:getlastmodified>Fri, 01 May 2026 01:00:00 GMT</d:getlastmodified></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>http://192.168.1.50:5005/backups/Spokes_Backup_20260502_010000.zip</d:href>
                <d:propstat><d:prop><d:getcontentlength>2048</d:getcontentlength><d:getlastmodified>Sat, 02 May 2026 01:00:00 GMT</d:getlastmodified></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>https://evil.tld/Spokes_Backup_20260503_010000.spkbackup</d:href>
                <d:propstat><d:prop><d:getcontentlength>4096</d:getcontentlength></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>//evil.tld/Spokes_Backup_20260504_010000.spkbackup</d:href>
                <d:propstat><d:prop><d:getcontentlength>4096</d:getcontentlength></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>http://192.168.1.50:9999/backups/Spokes_Backup_20260505_010000.spkbackup</d:href>
                <d:propstat><d:prop><d:getcontentlength>4096</d:getcontentlength></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>/backups/Spokes_Backup_20260506_010000.tmp.zip</d:href>
                <d:propstat><d:prop><d:getcontentlength>4096</d:getcontentlength></d:prop></d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var factory = CreateMockHttpClientFactory((req, _) =>
        {
            Assert.Equal("PROPFIND", req.Method.Method);
            Assert.Equal("http://192.168.1.50:5005/backups/", req.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(propfindXml, Encoding.UTF8, "application/xml")
            });
        });

        var provider = new WebDavBackupProvider(_profiles, factory);
        var items = await provider.ListBackupsAsync();

        Assert.Equal(2, items.Count);
        Assert.Equal("Spokes_Backup_20260502_010000.zip", items[0].FileName);
        Assert.Equal("http://192.168.1.50:5005/backups/Spokes_Backup_20260502_010000.zip", items[0].Id);
        Assert.Equal("Spokes_Backup_20260501_010000.spkbackup", items[1].FileName);
        Assert.Equal("http://192.168.1.50:5005/backups/Spokes_Backup_20260501_010000.spkbackup", items[1].Id);
    }

    [Fact]
    public async Task DownloadBackupAsync_And_DeleteBackupAsync_RejectCrossOriginUrlsWithZeroHttpRequests_AndRejectRedirects()
    {
        var profile = _profiles.Get();
        profile.Backup.WebDav.IsEnabled = true;
        profile.Backup.WebDav.ServerUrl = "https://nextcloud.lan/remote.php/dav/files/admin/backups/";
        profile.Backup.WebDav.Username = "nc-admin";
        profile.Backup.WebDav.Password = "TopSecretBasicAuthToken!";
        _profiles.Save(profile);

        var sentRequests = new List<Uri>();
        var factory = CreateMockHttpClientFactory((req, _) =>
        {
            sentRequests.Add(req.RequestUri!);
            var redirectResponse = new HttpResponseMessage(HttpStatusCode.Found);
            redirectResponse.Headers.Location = new Uri("https://evil.tld/capture-basic-auth");
            return Task.FromResult(redirectResponse);
        });

        var provider = new WebDavBackupProvider(_profiles, factory);

        // 1. Cross-origin, protocol-relative, scheme-downgraded, and cross-port URLs send ZERO HTTP requests
        Assert.Null(await provider.DownloadBackupAsync("https://evil.tld/Spokes_Backup_2026.spkbackup"));
        Assert.Null(await provider.DownloadBackupAsync("//evil.tld/Spokes_Backup_2026.spkbackup"));
        Assert.Null(await provider.DownloadBackupAsync("http://nextcloud.lan/remote.php/dav/files/admin/backups/Spokes_Backup_2026.spkbackup"));
        Assert.Null(await provider.DownloadBackupAsync("https://nextcloud.lan:8443/remote.php/dav/files/admin/backups/Spokes_Backup_2026.spkbackup"));

        await provider.DeleteBackupAsync("https://evil.tld/Spokes_Backup_2026.spkbackup");
        await provider.DeleteBackupAsync("//evil.tld/Spokes_Backup_2026.spkbackup");
        await provider.DeleteBackupAsync("http://nextcloud.lan/remote.php/dav/files/admin/backups/Spokes_Backup_2026.spkbackup");

        Assert.Empty(sentRequests);

        // 2. Same-origin URL returning 302 Found is rejected (not followed)
        var redirectDownload = await provider.DownloadBackupAsync(
            "https://nextcloud.lan/remote.php/dav/files/admin/backups/Spokes_Backup_2026.spkbackup");
        Assert.Null(redirectDownload);
        Assert.Single(sentRequests);
    }

    [Fact]
    public void AddSpokesCoreServices_ConfiguresWebDavBackupHttpClient_WithAllowAutoRedirectDisabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSpokesCoreServices();

        using var provider = services.BuildServiceProvider();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        using var handler = handlerFactory.CreateHandler("WebDavBackup");

        HttpMessageHandler current = handler;
        while (current is DelegatingHandler delegating && delegating.InnerHandler != null)
        {
            current = delegating.InnerHandler;
        }

        var primaryHandler = Assert.IsType<HttpClientHandler>(current);
        Assert.False(primaryHandler.AllowAutoRedirect);
    }
}
