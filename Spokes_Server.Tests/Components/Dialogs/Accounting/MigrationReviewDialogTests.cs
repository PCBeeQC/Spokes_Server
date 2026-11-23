using System.Security.Claims;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Dialogs.Accounting;
using Spokes_Server.Components.Pages.Admin;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Migrations;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Components.Dialogs.Accounting;

public class MigrationReviewDialogTests : BunitContext
{
    private readonly string _testDataDir;
    private readonly Database _db;
    private readonly IAccountingMigrationService _migrationService;

    public MigrationReviewDialogTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_MigrationDialog_{Guid.NewGuid():N}");
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
        Services.AddScoped<ILedgerService, LedgerService>();
        Services.AddScoped<ITaxRemittanceService, TaxRemittanceService>();
        Services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        Services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        Services.AddScoped<IAccountingService, AccountingService>();
        Services.AddScoped<IAccountingMigrationService, AccountingMigrationService>();
        Services.AddScoped<ClockifyMigrationService>();
        Services.AddScoped<Spokes_Server.Core.Services.Identity.UserService>();

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
            FirstName = "Admin",
            LastName = "User",
            IsActive = true,
            IsAdmin = true,
            Permissions = [AppPermissions.Admin.ManageSettings]
        });

        _migrationService = Services.GetRequiredService<IAccountingMigrationService>();
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
    public async Task MigrationReviewDialog_RendersAllTabsWithSageAndGlMappingData_WithoutUnhandledException()
    {
        Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();

        var adminActor = _db.Employees.GetById("admin-1");
        var preview = _migrationService.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.FreshCutover,
            Format = AccountingMigrationSourceFormat.AutoDetect,
            AsOfDate = new DateTime(2026, 6, 30),
            Files =
            [
                new MigrationUploadedFile
                {
                    FileName = "Accounts.txt",
                    Content = Encoding.UTF8.GetBytes("33002,3,Accounts\n1060,Banque TD,A,GIFI:1001\n")
                },
                new MigrationUploadedFile
                {
                    FileName = "Vendors.txt",
                    Content = Encoding.UTF8.GetBytes("33002,3,Vendors\n\"McMaster-Carr\",\"Sales\",\"300 Rd\",\"\",\"Aurora\",\"OH\",\"44202\",\"USA\",\"\",\"330-555-0199\",\"\",\"\",\"\",\"\",\"sales@mcmaster.com\",\"\",\"USD\"\n")
                },
                new MigrationUploadedFile
                {
                    FileName = "Customers.txt",
                    Content = Encoding.UTF8.GetBytes("33002,3,Customers\n\"Client Alpha Inc.\",\"Sophie\",\"50 Blvd\",\"\",\"Laval\",\"QC\",\"H7N 1A1\",\"Canada\",\"\",\"450-555-0155\",\"\",\"\",\"\",\"\",\"sophie@alpha.com\",\"\",\"CAD\"\n")
                },
                new MigrationUploadedFile
                {
                    FileName = "Employees.txt",
                    Content = Encoding.UTF8.GetBytes("33002,3,Employees\n\"Jean-Luc Picard\",\"200 Ave\",\"\",\"Montréal\",\"QC\",\"H2X 1Y4\",\"514-555-0102\",\"\",\"123-456-782\",\"01-01-1990\",\"15-03-2022\",\"QC\",\"26\"\n")
                },
                new MigrationUploadedFile
                {
                    FileName = "Projects.txt",
                    Content = Encoding.UTF8.GetBytes("33002,3,Project\n\"PRJ101 - Client Alpha Cell\",\"2026-02-01\",\"\"\n")
                }
            ]
        }, adminActor);

        // Ensure GlAccountSelect in Tab 4 is exercised too
        if (preview.GlMappings.Count > 0)
        {
            preview.GlMappings[0].Action = MigrationGlAction.MapToExisting;
            preview.GlMappings[0].TargetGlCode = "1020";
        }

        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<MigrationReviewDialog>
        {
            { x => x.Preview, preview },
            { x => x.ClockifyRecordCount, 0 }
        };

        await dialogProvider.InvokeAsync(() =>
            dialogService.ShowAsync<MigrationReviewDialog>("Review & Curate Import Data", parameters));

        Assert.Contains("Jean-Luc Picard", dialogProvider.Markup);

        // Click through all remaining tabs in SpokesTabs so Address Book, Projects, Chart of Accounts (GlAccountSelect), and Open AR/AP render too
        var tabHeaders = dialogProvider.FindAll(".mud-tab");
        Assert.Equal(5, tabHeaders.Count);
        for (var i = 1; i < tabHeaders.Count; i++)
        {
            await dialogProvider.InvokeAsync(() => dialogProvider.FindAll(".mud-tab")[i].Click());
        }

        Assert.Contains("Confirm &amp; Execute Import", dialogProvider.Markup);
    }
}
