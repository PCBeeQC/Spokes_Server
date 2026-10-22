using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.UI;
using Spokes_Server.Resources;
using Spokes_Server.Tests;
using Xunit;

namespace Spokes_Server.Tests.Localization;

public class LocalizationTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly IStringLocalizer<AppResources> _loc;
    private readonly IStringLocalizer<BusinessResources> _bizLoc;

    public LocalizationTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        services.AddLocalization();
        services.AddTransient<IStringLocalizer<BusinessResources>, Spokes_Server.Core.Services.UI.CompositeBusinessLocalizer>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _loc = _serviceProvider.GetRequiredService<IStringLocalizer<AppResources>>();
        _bizLoc = _serviceProvider.GetRequiredService<IStringLocalizer<BusinessResources>>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    private void SetEdition(string? edition)
    {
        var profile = _db.CompanyProfile.Get();
        profile.Edition = edition!;
        _db.CompanyProfile.Save(profile);
    }

    [Fact]
    public void Localizer_ResolvesEnglishStrings_WhenCultureIsEnUS()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");

            Assert.Equal("Save", _loc["Save"]);
            Assert.Equal("Cancel", _loc["Cancel"]);
            Assert.Equal("Delete", _loc["Delete"]);
            Assert.Equal("Language & Region", _loc["LanguageAndRegion"]);
            Assert.Equal("Select Language", _loc["SelectLanguage"]);
            Assert.Equal("Group", _loc["Group"]);
            Assert.Equal("Members", _loc["Members"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void Localizer_ResolvesGermanStrings_WhenCultureIsDeDE()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            Assert.Equal("Speichern", _loc["Save"]);
            Assert.Equal("Abbrechen", _loc["Cancel"]);
            Assert.Equal("Löschen", _loc["Delete"]);
            Assert.Equal("Sprache & Region", _loc["LanguageAndRegion"]);
            Assert.Equal("Sprache auswählen", _loc["SelectLanguage"]);
            Assert.Equal("Mitglieder", _loc["Members"]);
            Assert.Equal("Gruppe", _loc["Group"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void Localizer_FallsBackToNeutralGerman_WhenCultureIsRegionalGerman()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-AT"); // Austrian German falls back to de

            Assert.Equal("Speichern", _loc["Save"]);
            Assert.Equal("Abbrechen", _loc["Cancel"]);
            Assert.Equal("Löschen", _loc["Delete"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void Localizer_ReturnsKey_WhenStringNotFound()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            const string unknownKey = "NonExistentKey_XYZ_123";
            var localized = _loc[unknownKey];

            Assert.Equal(unknownKey, localized.Value);
            Assert.True(localized.ResourceNotFound);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void AppLabelService_WithLocalizer_TranslatesBusinessLabels_InGerman()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            SetEdition("Business");

            var service = new AppLabelService(_db.CoreContext, _loc);

            Assert.Equal("Team", service.Team);
            Assert.Equal("Teams", service.Teams);
            Assert.Equal("Mitarbeiter", service.Employee);
            Assert.Equal("Mitarbeiter", service.Employees);
            Assert.Equal("Favoriten", service.Favorites);
            Assert.Equal("Firma", service.Company);
            Assert.Equal("Geschäfts-E-Mail", service.Email);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void AppLabelService_WithLocalizer_TranslatesFamilyLabels_InGerman()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            SetEdition("Family");

            var service = new AppLabelService(_db.CoreContext, _loc);

            Assert.Equal("Gruppe", service.Team);
            Assert.Equal("Gruppen", service.Teams);
            Assert.Equal("Mitglied", service.Employee);
            Assert.Equal("Mitglieder", service.Employees);
            Assert.Equal("Menü", service.Favorites);
            Assert.Equal("Server", service.Company);
            Assert.Equal("E-Mail", service.Email);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void CultureController_SetCulture_AppendsCultureCookieAndRedirects()
    {
        var controller = new CultureController();
        var httpContext = new DefaultHttpContext();
        var mockUrl = new Mock<IUrlHelper>();
        mockUrl.Setup(u => u.IsLocalUrl("/chat")).Returns(true);
        mockUrl.Setup(u => u.IsLocalUrl("https://malicious.example.com")).Returns(false);

        controller.Url = mockUrl.Object;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = controller.SetCulture("de-DE", "/chat");

        var localRedirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/chat", localRedirect.Url);

        var setCookieHeader = httpContext.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains(CookieRequestCultureProvider.DefaultCookieName, setCookieHeader);
        Assert.Contains("c%3Den-US%7Cuic%3Dde-DE", setCookieHeader);
    }

    [Fact]
    public void CultureController_SetCulture_FallsBackToRoot_WhenRedirectUriIsExternalOrInvalid()
    {
        var controller = new CultureController();
        var httpContext = new DefaultHttpContext();
        var mockUrl = new Mock<IUrlHelper>();
        mockUrl.Setup(u => u.IsLocalUrl(It.IsAny<string>())).Returns(false);

        controller.Url = mockUrl.Object;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = controller.SetCulture("de-DE", "https://malicious.example.com");

        var localRedirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/", localRedirect.Url);
    }

    [Fact]
    public void DecoupledCulture_GuaranteesPeriodDecimal_WhileTranslatingUI()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;
        try
        {
            // Decoupled state: formatting culture is en-US (period decimal), UI culture is French/German
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            // Verify period decimal parsing works cleanly
            var parsed = decimal.Parse("12.50");
            Assert.Equal(12.50m, parsed);

            // Verify decimal formatting uses period
            Assert.Equal("12.50", 12.50m.ToString("F2"));

            // Verify UI string translates simultaneously
            Assert.Equal("Speichern", _loc["Save"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Fact]
    public void BizLoc_ResolvesBusinessStrings_WhenCultureIsEnUS()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");

            Assert.Equal("HR Details", _bizLoc["HR Details"]);
            Assert.Equal("Document Signature", _bizLoc["Document Signature"]);
            Assert.Equal("New Project", _bizLoc["New Project"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void BizLoc_FallsBackToAppResources_ForSharedActions()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");

            Assert.Equal("Save", _bizLoc["Save"]);
            Assert.Equal("Cancel", _bizLoc["Cancel"]);
            Assert.Equal("Delete", _bizLoc["Delete"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void BizLoc_ResolvesGermanStrings_WhenCultureIsDeDE()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            Assert.Equal("HR-Details", _bizLoc["HR Details"]);
            Assert.Equal("Dokumentsignatur", _bizLoc["Document Signature"]);
            Assert.Equal("Neues Projekt", _bizLoc["New Project"]);

            // Shared fallback translates in German too
            Assert.Equal("Speichern", _bizLoc["Save"]);
            Assert.Equal("Abbrechen", _bizLoc["Cancel"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void AppResources_DoesNotContainBusinessKeys()
    {
        // Crowdin resource (AppResources) must not contain business-only keys
        Assert.True(_loc["HR Details"].ResourceNotFound);
        Assert.True(_loc["Document Signature"].ResourceNotFound);
        Assert.True(_loc["New Project"].ResourceNotFound);
    }
}
