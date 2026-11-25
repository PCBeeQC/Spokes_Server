using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Dialogs.Accounting;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Components.Dialogs.Accounting;

[Collection("JurisdictionStaticState")]
public class InvoiceMasterDialogTests : BunitContext
{
    private readonly string _testDataDir;
    private readonly Database _db;

    public InvoiceMasterDialogTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_InvoiceDialog_{Guid.NewGuid():N}");
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
        Services.AddScoped<BillingOrchestrationService>();
        Services.AddScoped<UserService>();

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "acct-1"),
                new Claim("EmployeeId", "acct-1")
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
            Id = "acct-1",
            FirstName = "Sophie",
            LastName = "Martin",
            IsActive = true,
            IsAdmin = true,
            Permissions =
            [
                AppPermissions.Finance.AccessAccounting,
                AppPermissions.Finance.ApproveInvoices
            ]
        });

        var jurisdictionService = Services.GetRequiredService<IJurisdictionTemplateService>();
        jurisdictionService.ActivateTemplate("CA-QC", syncChartOfAccounts: false, actor: _db.Employees.GetById("acct-1"));

        _db.Projects.Save(new Project
        {
            Id = "proj-tax-exempt",
            Name = "Acme Demo Project",
            ProjectNumber = "PRJ-100"
        });
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
    public async Task TaxExemptSwitch_TogglesInvoiceTaxRateBetweenZeroAndPreviousRate_AndClearsTaxComponents()
    {
        Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();

        var invoice = new Invoice
        {
            ProjectId = "proj-tax-exempt",
            InvoiceNumber = "INV-2601-001",
            Status = InvoiceStatus.Draft,
            TaxRate = 0.14975m,
            TaxComponents =
            [
                new DocumentTaxLine { Code = "TPS", Name = "GST / TPS", Rate = 0.05m, Amount = 5.00m },
                new DocumentTaxLine { Code = "TVQ", Name = "QST / TVQ", Rate = 0.09975m, Amount = 9.98m }
            ]
        };

        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<InvoiceMasterDialog>
        {
            { x => x.Invoice, invoice },
            { x => x.CanEditDraft, true },
            { x => x.CanApproveInvoice, true }
        };

        await dialogProvider.InvokeAsync(() =>
            dialogService.ShowAsync<InvoiceMasterDialog>("Edit Invoice", parameters));

        var exemptSwitch = dialogProvider.FindComponents<MudSwitch<bool>>()
            .First(c => c.Instance.GetState(x => x.Value) == false);

        // Toggle Tax Exempt ON
        await dialogProvider.InvokeAsync(() => exemptSwitch.Instance.ValueChanged.InvokeAsync(true));

        Assert.Equal(0m, invoice.TaxRate);
        Assert.Empty(invoice.TaxComponents);
        Assert.True(exemptSwitch.Instance.GetState(x => x.Value));

        // Toggle Tax Exempt OFF -> restores 0.14975m
        await dialogProvider.InvokeAsync(() => exemptSwitch.Instance.ValueChanged.InvokeAsync(false));

        Assert.Equal(0.14975m, invoice.TaxRate);
        Assert.False(exemptSwitch.Instance.GetState(x => x.Value));
    }
}
