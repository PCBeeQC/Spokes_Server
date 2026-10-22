using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Identity;

public class CasdoorComponentsTests : IDisposable
{
    private readonly string _tempDbPath;

    public CasdoorComponentsTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"casdoor_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
        }
    }

    [Fact]
    public void CasdoorConfigTemplateGenerator_GenerateAppConf_ContainsExpectedDirectives()
    {
        var generator = new CasdoorConfigTemplateGenerator();
        var conf = generator.GenerateAppConf("https://test.spokes.sh", "/test/data", "prod");

        Assert.Contains("origin = \"https://test.spokes.sh\"", conf);
        Assert.Contains("runmode = prod", conf);
        Assert.Contains("driverName = sqlite", conf);
        Assert.Contains("dataSourceName = file:/test/data/casdoor.db?cache=shared", conf.Replace('\\', '/'));
    }

    [Fact]
    public void CasdoorConfigTemplateGenerator_GenerateInitDataJson_GeneratesValidJsonStructure()
    {
        var generator = new CasdoorConfigTemplateGenerator();
        var parameters = new CasdoorInitDataParameters(
            "https://test.spokes.sh",
            "admin",
            "hash1",
            "spokesadmin",
            "hash2",
            "client_123",
            "secret_456",
            "Acme Corp",
            "https://test.spokes.sh/logo.png",
            "https://test.spokes.sh/icon.png",
            "https://test.spokes.sh/spokes-org-logo.png",
            "https://test.spokes.sh/spokes-org-icon.png");

        var jsonStr = generator.GenerateInitDataJson(parameters);
        var root = JsonNode.Parse(jsonStr);

        Assert.NotNull(root);
        Assert.NotNull(root["organizations"]);
        Assert.NotNull(root["users"]);
        Assert.NotNull(root["applications"]);

        var orgs = root["organizations"]!.AsArray();
        Assert.Equal(2, orgs.Count);
        Assert.Equal("Acme Corp", orgs[1]!["displayName"]!.ToString());

        var apps = root["applications"]!.AsArray();
        Assert.Equal(2, apps.Count);
        Assert.Equal("client_123", apps[1]!["clientId"]!.ToString());
        Assert.Equal("secret_456", apps[1]!["clientSecret"]!.ToString());
    }

    [Fact]
    public async Task CasdoorApiClient_UpdateApplicationBrandingAsync_Success_UpdatesAndPosts()
    {
        var mockHttpHandler = new Mock<HttpMessageHandler>();
        var getResponseJson = @"{
            ""status"": ""ok"",
            ""data"": {
                ""name"": ""spokes"",
                ""displayName"": ""Old Name"",
                ""logo"": """",
                ""favicon"": """",
                ""redirectUris"": []
            }
        }";

        mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Get),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(getResponseJson)
            });

        mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new CasdoorApiClient(mockHttpClientFactory.Object);
        var request = new CasdoorApplicationBrandingRequest
        {
            Credentials = new CasdoorConnectionCredentials
            {
                BaseUrl = "http://127.0.0.1:8000",
                ClientId = "cid",
                ClientSecret = "csec"
            },
            CompanyName = "Updated Corp",
            LogoUrl = "https://url/logo.png",
            FaviconUrl = "https://url/fav.png",
            ServerPublicUrl = "https://public.spokes.sh"
        };
        var result = await client.UpdateApplicationBrandingAsync(request);

        Assert.True(result);
        mockHttpHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post && req.RequestUri!.ToString().Contains("update-application")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task CasdoorDatabaseMigrator_UpdateAdminCredentialsAndSanitize_ModifiesSqliteDb()
    {
        // 1. Create a minimal SQLite database simulating Casdoor schema
        using (var connection = new SqliteConnection($"Data Source={_tempDbPath}"))
        {
            await connection.OpenAsync();

            var initSql = @"
                CREATE TABLE `user` (name TEXT, owner TEXT, password TEXT, password_type TEXT, password_salt TEXT);
                INSERT INTO `user` VALUES ('admin', 'built-in', 'old_pass', 'plain', 'salt');
                INSERT INTO `user` VALUES ('spokesuser', 'spokes-org', 'old_pass', 'plain', 'salt');

                CREATE TABLE `application` (name TEXT, display_name TEXT, logo TEXT, favicon TEXT, enable_sign_up INTEGER, enable_auto_signin INTEGER, client_id TEXT, client_secret TEXT);
                INSERT INTO `application` (name, display_name, enable_sign_up) VALUES ('app-built-in', 'Built-in', 1);
                INSERT INTO `application` (name, display_name, enable_sign_up) VALUES ('spokes', 'Old Spokes', 0);

                CREATE TABLE `organization` (name TEXT, display_name TEXT, logo TEXT, favicon TEXT, has_privilege_consent INTEGER, website_url TEXT, default_avatar TEXT, account_items TEXT, nav_items TEXT, widget_items TEXT);
                INSERT INTO `organization` VALUES ('spokes-org', 'Old Org', '', '', 0, '', '', '[{""name"":""Organization"",""visible"":true},{""name"":""CustomField"",""visible"":true}]', '[]', '[]');
            ";

            var cmd = connection.CreateCommand();
            cmd.CommandText = initSql;
            await cmd.ExecuteNonQueryAsync();
        }

        var migrator = new CasdoorDatabaseMigrator();

        // 2. Test UpdateAdminCredentialsAndBrandingAsync
        var rows = await migrator.UpdateAdminCredentialsAndBrandingAsync(new CasdoorBrandingMigrationOptions
        {
            DbPath = _tempDbPath,
            BuiltInUsername = "admin",
            HashedBuiltInPassword = "new_hash_admin",
            SpokesUsername = "spokesuser",
            HashedSpokesPassword = "new_hash_spokes",
            CompanyName = "New Company",
            LogoUrl = "https://test/logo.png",
            FaviconUrl = "https://test/fav.png",
            FixedLogoUrl = "https://test/fixed-logo.png",
            FixedFaviconUrl = "https://test/fixed-fav.png",
            ClientId = "cid_123",
            ClientSecret = "sec_456"
        });

        Assert.True(rows > 0);

        // Verify password updated in DB
        using (var connection = new SqliteConnection($"Data Source={_tempDbPath}"))
        {
            await connection.OpenAsync();
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT password, password_type FROM `user` WHERE name = 'admin'";
            using var reader = await checkCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("new_hash_admin", reader.GetString(0));
            Assert.Equal("bcrypt", reader.GetString(1));
        }

        // 3. Test SanitizeOrganizationAsync
        var sanitized = await migrator.SanitizeOrganizationAsync(new CasdoorOrganizationSanitizationOptions
        {
            DbPath = _tempDbPath,
            CompanyName = "New Company",
            FixedLogoUrl = "https://test/fixed-logo.png",
            FixedFaviconUrl = "https://test/fixed-fav.png"
        });
        Assert.True(sanitized);

        // Verify account_items hidden
        using (var connection = new SqliteConnection($"Data Source={_tempDbPath}"))
        {
            await connection.OpenAsync();
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT account_items, nav_items FROM `organization` WHERE name = 'spokes-org'";
            using var reader = await checkCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var accountItemsJson = reader.GetString(0);
            var navItemsJson = reader.GetString(1);

            Assert.Contains(@"""name"":""CustomField"",""visible"":false", accountItemsJson);
            Assert.Contains("/home-top", navItemsJson);
        }
    }

    [Fact]
    public async Task CasdoorDatabaseMigrator_UpdateAdminCredentialsAndBrandingAsync_ReturnsZeroUntilSpokesInitDataIsSeeded()
    {
        // 1. Simulate early Casdoor boot: only built-in user and app-built-in exist;
        //    InitFromFile() has NOT yet inserted 'spokes' app or 'spokes-org'.
        using (var connection = new SqliteConnection($"Data Source={_tempDbPath}"))
        {
            await connection.OpenAsync();

            var initSql = @"
                CREATE TABLE `user` (name TEXT, owner TEXT, password TEXT, password_type TEXT, password_salt TEXT);
                INSERT INTO `user` VALUES ('admin', 'built-in', 'old_pass', 'plain', 'salt');

                CREATE TABLE `application` (name TEXT, display_name TEXT, logo TEXT, favicon TEXT, enable_sign_up INTEGER, enable_auto_signin INTEGER, client_id TEXT, client_secret TEXT);
                INSERT INTO `application` (name, display_name, enable_sign_up) VALUES ('app-built-in', 'Built-in', 1);

                CREATE TABLE `organization` (name TEXT, display_name TEXT, logo TEXT, favicon TEXT, has_privilege_consent INTEGER);
            ";

            var cmd = connection.CreateCommand();
            cmd.CommandText = initSql;
            await cmd.ExecuteNonQueryAsync();
        }

        var migrator = new CasdoorDatabaseMigrator();
        var options = new CasdoorBrandingMigrationOptions
        {
            DbPath = _tempDbPath,
            BuiltInUsername = "admin",
            HashedBuiltInPassword = "new_hash_admin",
            SpokesUsername = "spokesuser",
            HashedSpokesPassword = "new_hash_spokes",
            CompanyName = "New Company",
            LogoUrl = "https://test/logo.png",
            FaviconUrl = "https://test/fav.png",
            FixedLogoUrl = "https://test/fixed-logo.png",
            FixedFaviconUrl = "https://test/fixed-fav.png",
            ClientId = "expected_client_id",
            ClientSecret = "expected_client_secret"
        };

        // Act 1: Poll before InitFromFile() finishes
        var earlyRows = await migrator.UpdateAdminCredentialsAndBrandingAsync(options);

        // Assert 1: Must return 0 so CasdoorProvisioningService keeps polling
        Assert.Equal(0, earlyRows);

        // 2. Simulate Casdoor's InitFromFile() inserting 'spokes-org', 'spokesuser', and 'spokes' (with random client_id)
        using (var connection = new SqliteConnection($"Data Source={_tempDbPath}"))
        {
            await connection.OpenAsync();

            var seedSql = @"
                INSERT INTO `user` VALUES ('spokesuser', 'spokes-org', 'old_pass', 'plain', 'salt');
                INSERT INTO `application` (name, display_name, enable_sign_up, client_id, client_secret) VALUES ('spokes', 'Old Spokes', 0, 'casdoor_random_id', 'casdoor_random_secret');
                INSERT INTO `organization` (name, display_name, logo, favicon, has_privilege_consent) VALUES ('spokes-org', 'Old Org', '', '', 0);
            ";

            var cmd = connection.CreateCommand();
            cmd.CommandText = seedSql;
            await cmd.ExecuteNonQueryAsync();
        }

        // Act 2: Poll after InitFromFile() finishes
        var finalRows = await migrator.UpdateAdminCredentialsAndBrandingAsync(options);

        // Assert 2: All 5 records updated, including spokes client_id and client_secret
        Assert.Equal(5, finalRows);

        using (var connection = new SqliteConnection($"Data Source={_tempDbPath}"))
        {
            await connection.OpenAsync();
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT client_id, client_secret FROM `application` WHERE name = 'spokes'";
            using var reader = await checkCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("expected_client_id", reader.GetString(0));
            Assert.Equal("expected_client_secret", reader.GetString(1));
        }
    }
}
