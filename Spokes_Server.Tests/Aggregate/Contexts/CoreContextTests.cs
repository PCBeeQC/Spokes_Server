namespace Spokes_Server.Tests.Aggregate.Contexts;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Xunit;

public class CoreContextTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly CoreContext _context;
    private readonly CompanyProfileRepository _companyProfileRepo;

    public CoreContextTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _context = _serviceProvider.GetRequiredService<CoreContext>();
        _companyProfileRepo = _serviceProvider.GetRequiredService<CompanyProfileRepository>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Resolution_FromServiceProvider_ResolvesAndPopulatesAllRepositories()
    {
        Assert.NotNull(_context.CompanyProfile);
        Assert.NotNull(_context.DocumentTemplates);
        Assert.NotNull(_context.OpenIdAccounts);
        Assert.NotNull(_context.ServerConfigs);
        Assert.NotNull(_context.SystemConfigs);
        Assert.NotNull(_context.DeviceSessions);
        Assert.NotNull(_context.DemoConfigs);
        Assert.NotNull(_context.StandardDocuments);
    }

    [Fact]
    public void Config_WhenProfilePresent_DelegatesToCompanyProfile()
    {
        var profile = _companyProfileRepo.GetOrCreate();
        profile.Branding.CompanyName = "Spokes Corp";
        profile.Contact.AddressCity = "Montreal";
        profile.Backup.BackupTimeLocal = "03:00";
        profile.Licensing.LicensePayload = "SPK-123";
        profile.Storage.MaxFileUploadSizeBytes = 250000;
        profile.Auth.AutoCreateEmployeeOnFirstLogin = false;
        _companyProfileRepo.Save(profile);

        Assert.Equal("Spokes Corp", _context.GetBranding().CompanyName);
        Assert.Equal("Spokes Corp", _context.Branding.CompanyName);

        Assert.Equal("Montreal", _context.GetContact().AddressCity);
        Assert.Equal("Montreal", _context.Contact.AddressCity);

        Assert.Equal("03:00", _context.GetBackup().BackupTimeLocal);
        Assert.Equal("03:00", _context.Backup.BackupTimeLocal);

        Assert.Equal("SPK-123", _context.GetLicensing().LicensePayload);
        Assert.Equal("SPK-123", _context.Licensing.LicensePayload);

        Assert.Equal(250000, _context.GetStorage().MaxFileUploadSizeBytes);
        Assert.Equal(250000, _context.Storage.MaxFileUploadSizeBytes);

        Assert.False(_context.GetAuth().AutoCreateEmployeeOnFirstLogin);
        Assert.False(_context.Auth.AutoCreateEmployeeOnFirstLogin);
    }

    [Fact]
    public void Initialize_ExecutesWithoutExceptions()
    {
        var exception = Record.Exception(() => _context.Initialize());
        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WhenCompanyProfileNull_ReturnsDefaultConfigs()
    {
        var manualContext = new CoreContext(
            companyProfile: null!,
            _context.DocumentTemplates,
            _context.OpenIdAccounts,
            _context.ServerConfigs,
            _context.SystemConfigs,
            _context.DeviceSessions,
            _context.DemoConfigs,
            _context.StandardDocuments);

        Assert.NotNull(manualContext.GetBranding());
        Assert.NotNull(manualContext.Branding);
        Assert.NotNull(manualContext.GetContact());
        Assert.NotNull(manualContext.Contact);
        Assert.NotNull(manualContext.GetBackup());
        Assert.NotNull(manualContext.Backup);
        Assert.NotNull(manualContext.GetLicensing());
        Assert.NotNull(manualContext.Licensing);
        Assert.NotNull(manualContext.GetStorage());
        Assert.NotNull(manualContext.Storage);
        Assert.NotNull(manualContext.GetAuth());
        Assert.NotNull(manualContext.Auth);
    }
}
