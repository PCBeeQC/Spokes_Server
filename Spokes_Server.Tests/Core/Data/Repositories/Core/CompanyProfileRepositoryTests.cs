using SkiaSharp;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class CompanyProfileRepositoryTests : RepositoryTestFixture<CompanyProfileRepository>
{
    [Fact]
    public void Get_ReturnsDefaultProfile_WithoutMutatingDisk_WhenEmpty()
    {
        var profile = _repo.Get();

        Assert.NotNull(profile);
        Assert.Equal("GlobalProfile", profile.Id);
        Assert.Equal("My Spokes", profile.CompanyName);

        _writer.FlushAll();
        var expectedPath = Path.Combine(_testDataDir, "Settings", "company.json");
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void GetOrCreate_CreatesDefaultProfile_WhenEmpty()
    {
        var profile = _repo.GetOrCreate();

        Assert.NotNull(profile);
        Assert.Equal("GlobalProfile", profile.Id);
        Assert.Equal("My Spokes", profile.CompanyName);

        _writer.FlushAll();
        var expectedPath = Path.Combine(_testDataDir, "Settings", "company.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetOrCreate_DoesNotDownsample_OversizedIcon()
    {
        var dataUri = "data:image/png;base64," + new string('A', 60_000);
        var initialProfile = new CompanyProfile
        {
            Id = "GlobalProfile",
            CompanyName = "Test Company",
            IconBase64 = dataUri,
            IconVersion = 0
        };
        _repo.Save(initialProfile);

        // Fetch via GetOrCreate() which is pure read/bootstrap and must NOT downsample or increment version
        var profile = _repo.GetOrCreate();

        Assert.Equal(0, profile.IconVersion);
        Assert.Equal(dataUri, profile.IconBase64);
    }


    [Fact]
    public void Get_DoesNotMutateDiskOrDownsample_WhenReadingOversizedIcon()
    {
        var initialProfile = new CompanyProfile
        {
            Id = "GlobalProfile",
            CompanyName = "Test Company",
            IconBase64 = "data:image/png;base64," + new string('A', 60_000),
            IconVersion = 0
        };
        _repo.Save(initialProfile);

        // Get() is pure read and must not increment IconVersion
        var profile = _repo.Get();

        Assert.Equal(0, profile.IconVersion);
        Assert.Equal(initialProfile.IconBase64, profile.IconBase64);
    }


    [Fact]
    public void GetFilePath_SavesInSettingsCompanyJson()
    {
        var profile = new CompanyProfile { Id = "GlobalProfile", CompanyName = "Path Company" };
        _repo.Save(profile);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "company.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Get_ReturnsExistingProfileWithoutRecreating()
    {
        var profile = new CompanyProfile { Id = "GlobalProfile", CompanyName = "Custom Name", AddressCity = "Montreal" };
        _repo.Save(profile);

        var retrieved = _repo.Get();
        Assert.Equal("Custom Name", retrieved.CompanyName);
        Assert.Equal("Montreal", retrieved.AddressCity);
    }

    [Fact]
    public void DomainAccessors_ReturnExpectedDomainConfigs()
    {
        var profile = new CompanyProfile
        {
            Id = "GlobalProfile",
            CompanyName = "Accessor Company",
            AddressCity = "Toronto",
            QuotePrefix = "ACC-Q",
            DefaultTaxRate = 0.13m,
            MaxChatCacheSizeMb = 300,
            VapidSubject = "mailto:test@spokes.sh",
            BackupsEnabled = true,
            LicensePayload = "acc-license",
            MaxFileUploadSizeBytes = 12345,
            AutoCreateEmployeeOnFirstLogin = false
        };
        _repo.Save(profile);

        Assert.Equal("Accessor Company", _repo.GetBranding().CompanyName);
        Assert.Equal("Toronto", _repo.GetContact().AddressCity);
        Assert.Equal("ACC-Q", _repo.GetDocumentNumbering().QuotePrefix);
        Assert.Equal(0.13m, _repo.GetAccounting().DefaultTaxRate);
        Assert.Equal(300, _repo.GetChat().MaxChatCacheSizeMb);
        Assert.Equal("mailto:test@spokes.sh", _repo.GetPush().VapidSubject);
        Assert.True(_repo.GetBackup().BackupsEnabled);
        Assert.Equal("acc-license", _repo.GetLicensing().LicensePayload);
        Assert.Equal(12345, _repo.GetStorage().MaxFileUploadSizeBytes);
        Assert.False(_repo.GetAuth().AutoCreateEmployeeOnFirstLogin);
        Assert.NotNull(_repo.GetEmailSettings());
        Assert.NotNull(_repo.GetTimesheetSettings());
        Assert.NotNull(_repo.GetHRSettings());
    }

    [Fact]
    public void DomainContexts_ExposeDecomposedModuleConfigurations()
    {
        var profile = new CompanyProfile
        {
            Id = "GlobalProfile",
            CompanyName = "Context Test Corp",
            QuotePrefix = "CTX-Q",
            DefaultTaxRate = 0.15m,
            MaxChatCacheSizeMb = 500,
            AutoCreateEmployeeOnFirstLogin = false
        };
        _repo.Save(profile);

        var accountingCtx = new AccountingContext(null!, null!, null!, null!, null!, null!, null!, null!, _repo);
        Assert.Equal(0.15m, accountingCtx.Config.DefaultTaxRate);

        var hrCtx = new HrContext(null!, null!, null!, null!, null!, null!, _repo);
        Assert.NotNull(hrCtx.HRConfig);
        Assert.NotNull(hrCtx.TimesheetConfig);

        var commCtx = new CommunicationContext(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, _repo);
        Assert.Equal(500, commCtx.ChatConfig.MaxChatCacheSizeMb);
        Assert.NotNull(commCtx.PushConfig);
        Assert.NotNull(commCtx.EmailSettings);

        var projCtx = new ProjectsContext(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, _repo);
        Assert.Equal("CTX-Q", projCtx.DocumentNumbering.QuotePrefix);

        var coreCtx = new CoreContext(_repo, null!, null!, null!, null!, null!, null!, null!);
        Assert.Equal("Context Test Corp", coreCtx.Branding.CompanyName);
    }
}
