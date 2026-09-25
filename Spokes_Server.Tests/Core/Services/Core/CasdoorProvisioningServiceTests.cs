using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

public class CasdoorProvisioningServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly CasdoorProvisioningService _service;

    public CasdoorProvisioningServiceTests()
    {
        _mockConfig = new Mock<IConfiguration>();
        _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_mockConfig.Object);
        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();

        _service = new CasdoorProvisioningService(_db, _mockConfig.Object);
    }

    public override void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _serviceProvider.Dispose();

        try
        {
            var root = Path.GetPathRoot(Environment.CurrentDirectory);
            var ephemeralDir = Path.Combine(root ?? "C:\\", "app");
            if (Directory.Exists(ephemeralDir))
            {
                Directory.Delete(ephemeralDir, true);
            }
        }
        catch
        {
            // Ignore ephemeral directory cleanup issues
        }

        base.Dispose();
    }

    [Fact]
    public async Task ProvisionAsync_GeneratesCredentialsAndConfigurationFiles()
    {
        // Act
        var result = await _service.ProvisionAsync(
            "https://spokes.example.com",
            "AdminUser",
            "secret1",
            "SpokesUser",
            "secret2");

        // Assert - Return credentials tuple
        Assert.Equal("secret1", result.BuiltInPassword);
        Assert.Equal("secret2", result.SpokesPassword);
        Assert.Equal(20, result.ClientId.Length);
        Assert.Equal(40, result.ClientSecret.Length);

        // Assert - app.conf in {DataPath}/casdoor/conf/app.conf
        var appConfPath = Path.Combine(_testDataPath, "casdoor", "conf", "app.conf");
        Assert.True(File.Exists(appConfPath), "app.conf should exist in persistent conf dir");
        var appConfContent = await File.ReadAllTextAsync(appConfPath);
        Assert.Contains("origin = \"https://spokes.example.com\"", appConfContent);
        var expectedDbDataSource = $"dataSourceName = file:{Path.Combine(_testDataPath, "casdoor.db")}?cache=shared";
        Assert.Contains(expectedDbDataSource, appConfContent);

        // Assert - init_data.json in {DataPath}/casdoor/init_data.json
        var initDataPath = Path.Combine(_testDataPath, "casdoor", "init_data.json");
        Assert.True(File.Exists(initDataPath), "init_data.json should exist in persistent casdoor dir");
        var initDataContent = await File.ReadAllTextAsync(initDataPath);
        var initJson = JsonNode.Parse(initDataContent);
        Assert.NotNull(initJson);

        var users = initJson["users"]?.AsArray();
        Assert.NotNull(users);
        Assert.Equal(2, users.Count);
        Assert.Equal("adminuser", users[0]?["name"]?.ToString());
        Assert.Equal("built-in", users[0]?["owner"]?.ToString());
        Assert.Equal("spokesuser", users[1]?["name"]?.ToString());
        Assert.Equal("spokes-org", users[1]?["owner"]?.ToString());

        var apps = initJson["applications"]?.AsArray();
        Assert.NotNull(apps);
        Assert.Equal(2, apps.Count);
        Assert.Equal(result.ClientId, apps[1]?["clientId"]?.ToString());
        Assert.Equal(result.ClientSecret, apps[1]?["clientSecret"]?.ToString());

        // Assert - {DataPath}/casdoor.enabled
        var enabledFilePath = Path.Combine(_testDataPath, "casdoor.enabled");
        Assert.True(File.Exists(enabledFilePath), "casdoor.enabled should exist");
        var enabledContent = await File.ReadAllTextAsync(enabledFilePath);
        Assert.Equal("true", enabledContent);

        // Assert - _db.SystemConfigs.Get()
        var sysConfig = _db.SystemConfigs.Get();
        Assert.Equal(IdpType.BuiltInCasdoor, sysConfig.ProviderType);
        Assert.Equal("https://spokes.example.com", sysConfig.Authority);
        Assert.Equal(result.ClientId, sysConfig.ClientId);
        Assert.Equal(result.ClientSecret, sysConfig.ClientSecret);
        Assert.True(sysConfig.IsSetupComplete);
    }

    [Fact]
    public async Task ProvisionAsync_WithExistingSqliteDb_UpdatesAdminPasswordsAndAppBranding()
    {
        // Arrange: set company profile to verify custom company name propagation
        var profile = _db.CompanyProfile.Get();
        profile.CompanyName = "PolyRobotics";
        _db.CompanyProfile.Save(profile);

        // Create SQLite casdoor.db with minimal schema and pre-existing rows
        var dbPath = Path.Combine(_testDataPath, "casdoor.db");
        await CreateCasdoorDbForProvisioningAsync(dbPath);

        // Act
        var result = await _service.ProvisionAsync(
            "https://spokes.example.com",
            "AdminUser",
            "secret1",
            "SpokesUser",
            "secret2");

        // Assert - SQLite updates
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();

            // Check built-in user password
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT password, password_type, password_salt FROM user WHERE name = 'adminuser' AND owner = 'built-in'";
                using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                var hash = reader.GetString(0);
                Assert.True(BCrypt.Net.BCrypt.Verify("secret1", hash));
                Assert.Equal("bcrypt", reader.GetString(1));
                Assert.Equal(string.Empty, reader.GetString(2));
            }

            // Check spokes-org user password
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT password, password_type, password_salt FROM user WHERE name = 'spokesuser' AND owner = 'spokes-org'";
                using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                var hash = reader.GetString(0);
                Assert.True(BCrypt.Net.BCrypt.Verify("secret2", hash));
                Assert.Equal("bcrypt", reader.GetString(1));
                Assert.Equal(string.Empty, reader.GetString(2));
            }

            // Check app-built-in application
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT enable_sign_up, display_name, enable_auto_signin FROM application WHERE name = 'app-built-in'";
                using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt32(0));
                Assert.Equal("Casdoor Admin", reader.GetString(1));
                Assert.Equal(1, reader.GetInt32(2));
            }

            // Check spokes application
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT enable_sign_up, display_name, enable_auto_signin, client_id, client_secret FROM application WHERE name = 'spokes'";
                using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt32(0));
                Assert.Equal("PolyRobotics", reader.GetString(1));
                Assert.Equal(1, reader.GetInt32(2));
                Assert.Equal(result.ClientId, reader.GetString(3));
                Assert.Equal(result.ClientSecret, reader.GetString(4));
            }

            // Check organization
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT has_privilege_consent, logo, favicon FROM organization WHERE name = 'spokes-org'";
                using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt32(0));
                Assert.Equal("https://spokes.example.com/spokes-org-logo.png", reader.GetString(1));
                Assert.Equal("https://spokes.example.com/spokes-org-icon.png", reader.GetString(2));
            }
        }
    }

    [Fact]
    public async Task SyncBrandingAsync_WhenProviderNotBuiltInCasdoor_ReturnsImmediatelyWithoutModifyingFiles()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.ProviderType = IdpType.None;
        config.ServerPublicUrl = "https://external.example.com";
        config.IsCasdoorSanitized = false;
        _db.SystemConfigs.Save(config);

        // Act
        await _service.SyncBrandingAsync();

        // Assert
        var appConfPath = Path.Combine(_testDataPath, "casdoor", "conf", "app.conf");
        Assert.False(File.Exists(appConfPath), "app.conf should not be created when provider is not BuiltInCasdoor");
        Assert.False(_db.SystemConfigs.Get().IsCasdoorSanitized);
    }

    [Fact]
    public async Task SyncBrandingAsync_WhenProviderIsBuiltInCasdoor_UpdatesAppConfOrigin()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.ProviderType = IdpType.BuiltInCasdoor;
        config.ServerPublicUrl = "https://new.example.com";
        config.IsCasdoorSanitized = true; // DB already sanitized, only origin sync tested
        _db.SystemConfigs.Save(config);

        // Act
        await _service.SyncBrandingAsync();

        // Assert
        var appConfPath = Path.Combine(_testDataPath, "casdoor", "conf", "app.conf");
        Assert.True(File.Exists(appConfPath), "app.conf should be created");
        var appConfContent = await File.ReadAllTextAsync(appConfPath);
        Assert.Contains("origin = \"https://new.example.com\"", appConfContent);
    }

    [Fact]
    public async Task SyncBrandingAsync_WhenNotSanitizedAndCasdoorDbExists_SyncsOrganizationAndMarksSanitized()
    {
        // Arrange
        var profile = _db.CompanyProfile.Get();
        profile.CompanyName = "Test Organization";
        _db.CompanyProfile.Save(profile);

        var config = _db.SystemConfigs.Get();
        config.ProviderType = IdpType.BuiltInCasdoor;
        config.ServerPublicUrl = "https://spokes.example.com";
        config.IsCasdoorSanitized = false;
        _db.SystemConfigs.Save(config);

        var dbPath = Path.Combine(_testDataPath, "casdoor.db");
        await CreateCasdoorDbForSyncBrandingAsync(dbPath);

        // Act
        await _service.SyncBrandingAsync();

        // Assert - SystemConfigs marked sanitized
        var updatedConfig = _db.SystemConfigs.Get();
        Assert.True(updatedConfig.IsCasdoorSanitized, "IsCasdoorSanitized should be true after successful sync");

        // Assert - SQLite organization and application updated
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT display_name, logo, favicon, has_privilege_consent, website_url, default_avatar, account_items, nav_items, widget_items FROM organization WHERE name = 'spokes-org'";
                using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Test Organization", reader.GetString(0));
                Assert.Equal("https://spokes.example.com/spokes-org-logo.png", reader.GetString(1));
                Assert.Equal("https://spokes.example.com/spokes-org-icon.png", reader.GetString(2));
                Assert.Equal(1, reader.GetInt32(3));
                Assert.Equal("https://www.spokes.sh", reader.GetString(4));
                Assert.Equal("https://cdn.casbin.org/img/casbin.svg", reader.GetString(5));

                var accountItemsJson = JsonNode.Parse(reader.GetString(6))?.AsArray();
                Assert.NotNull(accountItemsJson);
                // Email is in keepItems, CustomField is not and should be visible: false
                Assert.True(accountItemsJson[0]?["visible"]?.GetValue<bool>());
                Assert.False(accountItemsJson[1]?["visible"]?.GetValue<bool>());

                var navItemsJson = JsonNode.Parse(reader.GetString(7))?.AsArray();
                Assert.NotNull(navItemsJson);
                Assert.Contains(navItemsJson, n => n?.ToString() == "/home-top");

                var widgetItemsJson = JsonNode.Parse(reader.GetString(8))?.AsArray();
                Assert.NotNull(widgetItemsJson);
                Assert.Contains(widgetItemsJson, w => w?.ToString() == "tour");
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT enable_sign_up FROM application WHERE name = 'spokes'";
                using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt32(0));
            }
        }
    }

    [Fact]
    public async Task SyncBrandingAsync_WhenNotSanitizedAndCasdoorDbDoesNotExist_DoesNotMarkSanitized()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.ProviderType = IdpType.BuiltInCasdoor;
        config.ServerPublicUrl = "https://spokes.example.com";
        config.IsCasdoorSanitized = false;
        _db.SystemConfigs.Save(config);

        var dbPath = Path.Combine(_testDataPath, "casdoor.db");
        if (File.Exists(dbPath)) File.Delete(dbPath);

        // Act
        await _service.SyncBrandingAsync();

        // Assert
        var updatedConfig = _db.SystemConfigs.Get();
        Assert.False(updatedConfig.IsCasdoorSanitized, "IsCasdoorSanitized should remain false when DB does not exist");
    }

    [Fact]
    public async Task SyncBrandingAsync_WhenServerPublicUrlIsEmpty_DoesNotWriteAppConf()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.ProviderType = IdpType.BuiltInCasdoor;
        config.ServerPublicUrl = string.Empty;
        config.IsCasdoorSanitized = true;
        _db.SystemConfigs.Save(config);

        // Act
        await _service.SyncBrandingAsync();

        // Assert
        var appConfPath = Path.Combine(_testDataPath, "casdoor", "conf", "app.conf");
        Assert.False(File.Exists(appConfPath), "app.conf should not be written when ServerPublicUrl is empty");
    }

    [Fact]
    public void Constructor_WhenDataPathNotConfigured_DefaultsToData()
    {
        // Arrange
        var emptyConfig = new Mock<IConfiguration>();
        emptyConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        // Act & Assert (instantiation succeeds with default DataPath)
        var service = new CasdoorProvisioningService(_db, emptyConfig.Object);
        Assert.NotNull(service);
    }

    private static async Task CreateCasdoorDbForProvisioningAsync(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE user (name TEXT, owner TEXT, password TEXT, password_type TEXT, password_salt TEXT);
            CREATE TABLE application (name TEXT, enable_sign_up INTEGER, display_name TEXT, logo TEXT, favicon TEXT, enable_auto_signin INTEGER, client_id TEXT, client_secret TEXT);
            CREATE TABLE organization (name TEXT, has_privilege_consent INTEGER, logo TEXT, favicon TEXT);

            INSERT INTO user (name, owner, password, password_type, password_salt) VALUES ('adminuser', 'built-in', 'oldpass', 'plain', 'salt');
            INSERT INTO user (name, owner, password, password_type, password_salt) VALUES ('spokesuser', 'spokes-org', 'oldpass', 'plain', 'salt');
            INSERT INTO application (name, enable_sign_up, display_name, logo, favicon, enable_auto_signin, client_id, client_secret) VALUES ('app-built-in', 1, 'Old Admin', 'old.png', 'old.ico', 0, '', '');
            INSERT INTO application (name, enable_sign_up, display_name, logo, favicon, enable_auto_signin, client_id, client_secret) VALUES ('spokes', 1, 'Old Spokes', 'old.png', 'old.ico', 0, 'old_id', 'old_secret');
            INSERT INTO organization (name, has_privilege_consent, logo, favicon) VALUES ('spokes-org', 0, 'old_logo.png', 'old_fav.png');
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateCasdoorDbForSyncBrandingAsync(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE organization (
                name TEXT,
                display_name TEXT,
                logo TEXT,
                favicon TEXT,
                has_privilege_consent INTEGER,
                website_url TEXT,
                default_avatar TEXT,
                account_items TEXT,
                nav_items TEXT,
                widget_items TEXT
            );
            CREATE TABLE application (
                name TEXT,
                enable_sign_up INTEGER
            );

            INSERT INTO organization (name, display_name, logo, favicon, has_privilege_consent, website_url, default_avatar, account_items, nav_items, widget_items)
            VALUES ('spokes-org', 'Old Org', 'old.png', 'old.ico', 0, '', '', '[{""name"":""Email"",""visible"":true},{""name"":""CustomField"",""visible"":true}]', '[]', '[]');
            INSERT INTO application (name, enable_sign_up) VALUES ('spokes', 0);
        ";
        await cmd.ExecuteNonQueryAsync();
    }
}
