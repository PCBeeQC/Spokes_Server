using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Dialogs.Admin;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Components.Dialogs.Admin;

[Collection("JurisdictionStaticState")]
public class TemplateEditorDialogTests : BunitContext
{
    private readonly string _testDataDir;
    private readonly Database _db;

    public TemplateEditorDialogTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_TemplateEditor_{Guid.NewGuid():N}");
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
        Services.AddSingleton<IJurisdictionTemplateService, JurisdictionTemplateService>();
        Services.AddSingleton<MarkdownSanitizerService>();
        Services.AddScoped<UserService>();

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                new Claim("EmployeeId", "admin-1")
            ],
            authenticationType: "TestAuth");
        var mockAuthProvider = new Mock<AuthenticationStateProvider>();
        mockAuthProvider
            .Setup(p => p.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        Services.AddSingleton<AuthenticationStateProvider>(mockAuthProvider.Object);

        _db = Services.GetRequiredService<Database>();
        _db.Initialize();
        _db.Employees.Save(new Employee
        {
            Id = "admin-1",
            FirstName = "Alex",
            LastName = "Tremblay",
            IsActive = true,
            IsAdmin = true,
            Permissions = [AppPermissions.Business.ManageSettings]
        });

        var jurisdictionService = Services.GetRequiredService<IJurisdictionTemplateService>();
        jurisdictionService.ActivateTemplate("CA-QC", syncChartOfAccounts: false, actor: _db.Employees.GetById("admin-1"));
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
    public async Task TemplateEditorDialog_OmitsTaxRate_PreservesBankingInfo_AndEditsTaxRegistrationLabels()
    {
        Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();

        var template = new DocumentTemplate
        {
            Name = "Standard Français",
            LanguageCode = "fr-CA",
            BankName = "Banque Demo",
            BankSwiftCode = "DEMOCCMM",
            BankAccountNumber = "000123456"
        };

        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<TemplateEditorDialog>
        {
            { x => x.Template, template }
        };

        await dialogProvider.InvokeAsync(() =>
            dialogService.ShowAsync<TemplateEditorDialog>("Edit Template", parameters));

        // Tax Rate % field must no longer exist, while Banking fields remain
        Assert.DoesNotContain("Tax Rate %", dialogProvider.Markup);
        Assert.Contains("Banking (Wire Info)", dialogProvider.Markup);
        Assert.Contains("Bank Name", dialogProvider.Markup);
        Assert.Contains("Swift / BIC / Transit", dialogProvider.Markup);
        Assert.Contains("Account / IBAN Number", dialogProvider.Markup);

        // Switch to "Labels / Translations" tab
        var labelsTab = dialogProvider.FindAll(".mud-tab").First(t => t.TextContent.Contains("Labels / Translations"));
        await dialogProvider.InvokeAsync(() => labelsTab.Click());

        Assert.Contains("Label: TPS Registration #", dialogProvider.Markup);
        Assert.Contains("Label: TVQ Registration #", dialogProvider.Markup);

        // Find the TPS registration label input by its label text and change it
        var tpsField = dialogProvider.FindComponents<MudTextField<string>>()
            .First(c => c.Instance.Label == "Label: TPS Registration #");
        await dialogProvider.InvokeAsync(() => tpsField.Instance.ValueChanged.InvokeAsync("No de TPS :"));

        Assert.Equal("No de TPS :", template.TaxRegistrationLabelsByCode["TPS"]);

        // Clearing the label removes the override key so default fallback applies
        await dialogProvider.InvokeAsync(() => tpsField.Instance.ValueChanged.InvokeAsync("   "));
        Assert.False(template.TaxRegistrationLabelsByCode.ContainsKey("TPS"));
    }
}
