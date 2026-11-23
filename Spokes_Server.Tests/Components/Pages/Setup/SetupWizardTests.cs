using System;
using System.IO;
using System.Net.Http;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Pages.Setup;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.UI;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Setup;

public class SetupWizardTests : BunitContext
{
    private readonly string _testDataDir;
    private readonly Database _db;

    public SetupWizardTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_SetupWizard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDir);

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddLogging();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        Services.AddSingleton<IConfiguration>(mockConfig.Object);

        Services.AddSingleton<EncryptionService>();
        Services.AddSpokesDatabase();
        Services.AddSingleton(new VersionMetadata("2026.11.1"));
        Services.AddSingleton<LicenseValidationService>();
        Services.AddSingleton<CasdoorProvisioningService>();
        Services.AddSingleton<AppLabelService>(sp => new AppLabelService(sp.GetRequiredService<Database>().CoreContext));
        Services.AddSingleton<IFileService>(Mock.Of<IFileService>());
        Services.AddSingleton<IHttpClientFactory>(Mock.Of<IHttpClientFactory>());
        Services.AddSingleton<IHostApplicationLifetime>(Mock.Of<IHostApplicationLifetime>());
        Services.AddSingleton<ICompanyBrandingService>(Mock.Of<ICompanyBrandingService>());
        Services.AddSingleton<BackupService>(sp =>
            new Mock<BackupService>(
                Mock.Of<ILogger<BackupService>>(),
                sp.GetRequiredService<CompanyProfileRepository>(),
                mockConfig.Object,
                null).Object);

        _db = Services.GetRequiredService<Database>();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch { }
            }
        }
        base.Dispose(disposing);
    }

    [Fact]
    public void WhenSetupAlreadyComplete_OnInitialized_RedirectsToRootAndRendersNoWizard()
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = true;
        _db.SystemConfigs.Save(config);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/setup");

        // Act
        var cut = Render<SetupWizard>();

        // Assert
        Assert.Equal("http://localhost/", nav.Uri);
        Assert.DoesNotContain("Spokes Initial Setup", cut.Markup);
        Assert.DoesNotContain("New Installation", cut.Markup);
    }

    [Fact]
    public void WhenSetupCompletedAfterInitialRender_StepNavigationIsBlockedAndLocksWizard()
    {
        // Arrange: initially incomplete so wizard renders step 0
        var config = _db.SystemConfigs.Get();
        config.IsSetupComplete = false;
        _db.SystemConfigs.Save(config);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/setup");

        var cut = Render<SetupWizard>();
        Assert.Contains("New Installation", cut.Markup);

        // Simulate setup completing in another tab/request
        var updated = _db.SystemConfigs.Get();
        updated.IsSetupComplete = true;
        _db.SystemConfigs.Save(updated);

        // Act: click "New Installation" card
        var cards = cut.FindAll(".mud-card");
        Assert.NotEmpty(cards);
        cards[0].Click();

        // Assert: wizard locked immediately, did not advance to EULA, redirected to root
        Assert.Equal("http://localhost/", nav.Uri);
        Assert.DoesNotContain("End User License Agreement", cut.Markup);
        Assert.DoesNotContain("Spokes Initial Setup", cut.Markup);
    }
}
