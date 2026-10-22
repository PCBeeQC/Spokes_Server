using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Tests;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

[Collection("EnvironmentVariableTests")]
public class ServerBootCoordinatorTests : TestDataTestBase
{
    public override void Dispose()
    {
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);
        base.Dispose();
    }

    [Fact]
    public async Task InitializeSpokesServerAsync_WhenMasterPasswordNotConfigured_ThrowsFatalConfigurationException()
    {
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);

        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns((string?)null);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<ICryptoService, CryptoService>();

        using var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<Database>();
        var profile = db.CompanyProfile.GetOrCreate();
        profile.PublicChannelMasterPassword = string.Empty;
        db.CompanyProfile.Save(profile);

        await Assert.ThrowsAsync<FatalConfigurationException>(() => sp.InitializeSpokesServerAsync());
    }

    [Fact]
    public async Task InitializeSpokesServerAsync_WhenMasterPasswordConfigured_InitializesSuccessfully()
    {
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", null);

        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("TestMasterPassword123!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<ICryptoService, CryptoService>();

        using var sp = services.BuildServiceProvider();

        var exception = await Record.ExceptionAsync(() => sp.InitializeSpokesServerAsync());
        Assert.Null(exception);

        var db = sp.GetRequiredService<Database>();
        var profile = db.CompanyProfile.Get();
        Assert.False(string.IsNullOrEmpty(profile.VapidPublicKey));
        Assert.False(string.IsNullOrEmpty(profile.VapidPrivateKey));
    }

    [Fact]
    public async Task InitializeSpokesServerAsync_WhenLicenseValidationRegistered_EnsuresDatabaseCreationIdentity()
    {
        Environment.SetEnvironmentVariable("SPOKES_MASTER_PASSWORD", "TestMasterPassword123!");

        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("TestMasterPassword123!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton(new VersionMetadata("2026.4.1"));
        services.AddSingleton<LicenseValidationService>();

        using var sp = services.BuildServiceProvider();
        await sp.InitializeSpokesServerAsync();

        var db = sp.GetRequiredService<Database>();
        var serverConfig = db.ServerConfigs.GetOrCreateGlobalConfig();
        Assert.False(string.IsNullOrEmpty(serverConfig.DatabaseCreationVersion));
        Assert.False(string.IsNullOrEmpty(serverConfig.DatabaseCreationId));
    }
}
