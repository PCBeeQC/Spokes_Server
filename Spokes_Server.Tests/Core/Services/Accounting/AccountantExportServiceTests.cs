namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.Export;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;
using Xunit;

public class AccountantExportServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ILedgerService _ledger;
    private readonly IAccountantExportService _export;

    public AccountantExportServiceTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<IJurisdictionTemplateService, JurisdictionTemplateService>();
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<ITaxRemittanceService, TaxRemittanceService>();
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        services.AddScoped<IAccountantExportService, AccountantExportService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _db.Initialize();
        _serviceProvider.GetRequiredService<IJurisdictionTemplateService>().EnsureChartOfAccountsSeeded();
        _ledger = _serviceProvider.GetRequiredService<ILedgerService>();
        _export = _serviceProvider.GetRequiredService<IAccountantExportService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void GenerateAccountantPackageZip_IncludesAll15Files_AndResolvesClientNameInAgedReceivables()
    {
        var proj = new Project
        {
            Id = "proj-1",
            ProjectNumber = "PRJ001",
            Name = "Robotics Cell",
            Client = new ClientInfo { BusinessName = "Acme Robotics Inc." }
        };
        _db.Projects.Save(proj);

        var inv = new Invoice
        {
            Id = "inv-1",
            InvoiceNumber = "INV-2026-001",
            ProjectId = proj.Id,
            Date = new DateTime(2026, 3, 10),
            DueDate = new DateTime(2026, 4, 10),
            Status = InvoiceStatus.Sent,
            Note = "Internal invoice memo",
            LineItems = [new InvoiceLine { Description = "Engineering", Quantity = 1, UnitPrice = 2500m }]
        };
        _db.Invoices.Save(inv);
        _ledger.RebuildOperationalLedger();

        _db.Employees.Save(new Employee
        {
            Id = "emp-1",
            FirstName = "Marc",
            LastName = "Gagnon",
            HourlyRate = 60m
        });
        _db.Timesheets.Save(new Timesheet
        {
            EmployeeId = "emp-1",
            EmployeeName = "Marc Gagnon",
            Year = 2026,
            WeekNumber = 11,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { ProjectId = proj.Id, Date = new DateTime(2026, 3, 12), Hours = 12m, IsBillable = true }
            ]
        });

        var zipBytes = _export.GenerateAccountantPackageZip(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            includeVouchers: false,
            actor: new Employee { Id = "cpa-admin", FirstName = "CPA", LastName = "Admin", IsActive = true, IsAdmin = true });
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entryNames = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("00_CPA_README.txt", entryNames);
        Assert.Contains("01_Trial_Balance.csv", entryNames);
        Assert.Contains("02_General_Ledger_Transactions.csv", entryNames);
        Assert.Contains("03_Income_Statement_PL.csv", entryNames);
        Assert.Contains("04_Balance_Sheet.csv", entryNames);
        Assert.Contains("05_Aged_Receivables_And_Payables.csv", entryNames);
        Assert.Contains("06_Sales_Tax_Summary.csv", entryNames);
        Assert.Contains("07_Payroll_And_DAS_Summary.csv", entryNames);
        Assert.Contains("08_Business_Overhead_And_Project_Allocation.csv", entryNames);
        Assert.Contains("09_GIFI_Schedule_100_125.csv", entryNames);
        Assert.Contains("09_GIFI_Tax_Import.gff", entryNames);
        Assert.Contains("10_Fixed_Assets_And_CapEx_Schedule.csv", entryNames);
        Assert.Contains("11_Bank_Reconciliation_Summary.csv", entryNames);
        Assert.Contains("12_Project_And_RnD_Labor_Schedule.csv", entryNames);
        Assert.Contains("13_Sage50_Import_Ready.imp", entryNames);

        using var arReader = new StreamReader(zip.GetEntry("05_Aged_Receivables_And_Payables.csv")!.Open());
        var arCsv = arReader.ReadToEnd();
        Assert.Contains("Acme Robotics Inc.", arCsv);
        Assert.DoesNotContain("Internal invoice memo", arCsv);

        using var rndReader = new StreamReader(zip.GetEntry("12_Project_And_RnD_Labor_Schedule.csv")!.Open());
        var rndCsv = rndReader.ReadToEnd();
        Assert.Contains("PRJ001", rndCsv);
        Assert.Contains("720.00", rndCsv);
    }
}
