using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.Export;
using Spokes_Server.Tests;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class Sage50ExportServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Sage50ExportService _exportService;
    private readonly Sage50ExportConfig _config;

    public Sage50ExportServiceTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _exportService = new Sage50ExportService(_db);

        _config = new Sage50ExportConfig
        {
            VersionHeader = "12001",
            CountryCode = "1",
            AccountsReceivableGl = "1200",
            AccountsPayableGl = "2100",
            DefaultSalesRevenueGl = "4100",
            DefaultExpenseGl = "5000",
            DefaultRecurringExpenseGl = "5600",
            DefaultEmployeeExpenseGl = "5750",
            DefaultKilometrageGl = "5755",
            DefaultCommissionGl = "5800",
            TpsTaxCode = "TPS",
            TpsTaxAccount = "2210",
            TvqTaxCode = "TVQ",
            TvqTaxAccount = "2220",
            CombinedTaxCode = "Q",
            IncludeNewCustomersAndVendorsInImp = true
        };
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void ValidateForExport_MissingGlAccounts_ReturnsErrors()
    {
        var invalidConfig = new Sage50ExportConfig
        {
            AccountsReceivableGl = "",
            AccountsPayableGl = ""
        };

        var result = _exportService.ValidateForExport([], new List<Bill>(), invalidConfig);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Accounts Receivable GL"));
        Assert.Contains(result.Errors, e => e.Contains("Accounts Payable GL"));
    }

    [Fact]
    public void GenerateSage50Imp_WithInvoicesAndBills_FormatsStrictSpecSyntax()
    {
        var project = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Automation Project",
            Client = new ClientInfo
            {
                BusinessName = "Client Corporation Inc",
                BusinessAddressNumber = "123",
                BusinessAddressStreet = "Main St",
                BusinessAddressCity = "Montréal",
                BusinessAddressState = "QC",
                BusinessAddressZip = "H2Z 1A1"
            }
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = project.Id,
            InvoiceNumber = "INV-2026-101",
            Date = new DateTime(2026, 4, 1),
            DueDate = new DateTime(2026, 5, 1),
            Status = InvoiceStatus.Sent,
            LineItems =
            [
                new InvoiceLine { Description = "Engineering Consulting", Quantity = 10, UnitPrice = 100m }
            ]
        };
        _db.Invoices.Save(invoice);

        var bill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = "BILL-SUP-55",
            SupplierName = "Industrial Suppliers QC",
            Date = new DateTime(2026, 4, 5),
            DueDate = new DateTime(2026, 5, 5),
            Amount = 574.88m,
            SubTotal = 500.00m,
            TaxAmount = 74.88m,
            TpsAmount = 25.00m,
            TvqAmount = 49.88m,
            Status = BillStatus.Unpaid,
            LineItems =
            [
                new BillLineItem { Description = "Hydraulic Fittings", Quantity = 5, UnitPrice = 100m }
            ]
        };
        _db.Bills.Save(bill);

        var imp = _exportService.GenerateSage50Imp([invoice], [bill], _config);

        Assert.NotNull(imp);
        // Official multi-line <Version> block
        Assert.Contains("<Version>\r\n\"12001\", \"1\"\r\n</Version>", imp);

        // Optional <customers> and <vendors> definitions
        Assert.Contains("<customers>", imp);
        Assert.Contains("\"Client Corporation Inc\"", imp);
        Assert.Contains("\"123 Main St\"", imp);
        Assert.Contains("<vendors>", imp);
        Assert.Contains("\"Industrial Suppliers QC\"", imp);

        // Sales Invoice block with Line 2 count = "1" and 5-field tax tuples on Line 3
        Assert.Contains("<SalInvoice>", imp);
        Assert.Contains("\"INV-2026-101\"", imp);
        Assert.Contains("04-01-2026", imp);
        Assert.Contains("\"1\", \"\", \"INV-2026-101\", \"04-01-2026\", \"0\", \"\", \"1149.75\", \"0.00\"", imp);
        Assert.Contains("\"4100\", \"10.00\", \"100.00\", \"1000.00\", \"TPS\", \"0\", \"1\", \"5.00\", \"50.00\", \"TVQ\", \"0\", \"1\", \"9.975\", \"99.75\"", imp);
        Assert.Contains("</SalInvoice>", imp);

        // Purchase Invoice block with Line 2 count = "1" and 5-field tax tuples on Line 3
        Assert.Contains("<PurInvoice>", imp);
        Assert.Contains("\"BILL-SUP-55\"", imp);
        Assert.Contains("04-05-2026", imp);
        Assert.Contains("\"1\", \"\", \"BILL-SUP-55\", \"04-05-2026\", \"574.88\", \"0.00\", \"0\", \"30\", \"0.00\"", imp);
        Assert.Contains("\"5000\", \"5.00\", \"100.00\", \"500.00\", \"TPS\", \"0\", \"1\", \"5.00\", \"25.00\", \"TVQ\", \"0\", \"1\", \"9.975\", \"49.88\"", imp);
        Assert.Contains("</PurInvoice>", imp);
    }

    [Fact]
    public void GenerateSage50ImpBytes_EncodesAccentsInLatin1Windows1252()
    {
        var bill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = "HQ-2026-01",
            SupplierName = "Hydro-Québec",
            Date = new DateTime(2026, 4, 1),
            Amount = 114.98m,
            SubTotal = 100.00m,
            TpsAmount = 5.00m,
            TvqAmount = 9.98m
        };

        var bytes = _exportService.GenerateSage50ImpBytes([], [_exportService.ConvertBillToExportItem(bill)], _config);
        var decodedLatin1 = Encoding.Latin1.GetString(bytes);

        Assert.Contains("Hydro-Québec", decodedLatin1);
        // Ensure 'é' is encoded as 1 byte (0xE9 in Latin1) and not UTF-8 0xC3 0xA9 ("Ã©" when read as Latin1)
        Assert.DoesNotContain("Ã©", decodedLatin1);
    }

    [Fact]
    public void TaxExemptInvoiceAndBill_ExportsZeroTaxesWithExemptFlag()
    {
        var group = new ProjectGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = "US Export Group",
            Client = new ClientInfo { BusinessName = "US Corp LLC" }
        };
        _db.ProjectGroups.Save(group);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid().ToString(),
            ProjectGroupId = group.Id,
            InvoiceNumber = "INV-EXEMPT-1",
            Date = new DateTime(2026, 4, 10),
            TaxRate = 0m,
            LineItems = [new InvoiceLine { Description = "Export Service", Quantity = 1, UnitPrice = 1000m }]
        };

        Assert.Equal(0m, invoice.TpsAmount);
        Assert.Equal(0m, invoice.TvqAmount);
        Assert.Equal(1000m, invoice.GrandTotal);

        var exemptBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = "RENT-APR",
            SupplierName = "Landlord Inc",
            Date = new DateTime(2026, 4, 1),
            Amount = 2000m,
            SubTotal = 2000m,
            IsTaxExempt = true
        };

        var imp = _exportService.GenerateSage50Imp([invoice], [exemptBill], _config);

        Assert.Contains("\"US Corp LLC\"", imp);
        Assert.Contains("\"4100\", \"1.00\", \"1000.00\", \"1000.00\", \"TPS\", \"1\", \"1\", \"0.00\", \"0.00\", \"TVQ\", \"1\", \"1\", \"0.00\", \"0.00\"", imp);
        Assert.Contains("\"5000\", \"1.00\", \"2000.00\", \"2000.00\", \"TPS\", \"1\", \"1\", \"0.00\", \"0.00\", \"TVQ\", \"1\", \"1\", \"0.00\", \"0.00\"", imp);
    }

    [Fact]
    public void GetAllPayableExportItems_AggregatesAllFourPayableStreamsAndPersistsExportStatus()
    {
        // 1. Bill
        var bill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = "B-100",
            SupplierName = "Vendor A",
            Date = new DateTime(2026, 4, 2),
            Amount = 114.98m,
            Status = BillStatus.Approved
        };
        _db.Bills.Save(bill);

        // 2. RecurringExpense (actual, non-estimate)
        var rec = new RecurringExpense
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Internet Fiber",
            Supplier = "Bell Canada",
            IsEstimate = false,
            GlAccountCode = "5610",
            History =
            [
                new RecurringBillRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    Date = new DateTime(2026, 4, 3),
                    InvoiceNumber = "BELL-04",
                    Amount = 114.98m
                }
            ]
        };
        _db.RecurringExpenses.Save(rec);

        // 3. ExpenseReport (Submitted because DateSubmitted is set and DatePaid is null)
        var report = new ExpenseReport
        {
            Id = Guid.NewGuid().ToString(),
            ReportNumber = "EXP-2026-01",
            EmployeeName = "Jean Tremblay",
            DateSubmitted = new DateTime(2026, 4, 4),
            LineItems =
            [
                new ExpenseItem { Description = "Client Lunch", Amount = 57.49m, IsKilometrage = false },
                new ExpenseItem { Description = "Site Visit", Kilometers = 100, KilometrageRate = 0.70m, IsKilometrage = true, IsTaxExempt = true }
            ]
        };
        _db.ExpenseReports.Save(report);

        // 4. CommissionLedgerRecord (Clawback -> Credit Note)
        var comm = new CommissionLedgerRecord
        {
            Id = Guid.NewGuid().ToString(),
            BeneficiaryName = "Marc Gagnon",
            InvoiceId = "INV-900",
            DateGenerated = new DateTime(2026, 4, 5),
            CommissionAmount = 250.00m,
            Status = CommissionStatus.Clawback
        };
        _db.CommissionLedgerRecords.Save(comm);

        var items = _exportService.GetAllPayableExportItems();
        Assert.Equal(4, items.Count);

        var commItem = items.Single(x => x.SourceType == SagePayableSourceType.Commission);
        Assert.True(commItem.IsCreditNote);

        var imp = _exportService.GenerateSage50Imp([], items, _config);
        Assert.Contains("\"Bell Canada\"", imp);
        Assert.Contains("\"5610\"", imp);
        Assert.Contains("\"Jean Tremblay\"", imp);
        Assert.Contains("\"Marc Gagnon\"", imp);
        Assert.Contains("\"-250.00\"", imp);

        // Mark all as exported
        _exportService.MarkAsExported([], items, "batch-all-4");

        var afterExportItems = _exportService.GetAllPayableExportItems();
        Assert.Equal(4, afterExportItems.Count);
        Assert.All(afterExportItems, x =>
        {
            Assert.True(x.IsExported);
            Assert.Equal("batch-all-4", x.SageBatchId);
        });

        // Unmark all
        _exportService.UnmarkExported([], afterExportItems);
        Assert.All(_exportService.GetAllPayableExportItems(), x => Assert.False(x.IsExported));
    }

    [Fact]
    public void ImportSageBaselineFile_ImportsVendorsAccountsAndAutoMaps()
    {
        var project = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Proj",
            Client = new ClientInfo { BusinessName = "Acme Robotics Inc." }
        };
        _db.Projects.Save(project);

        var customerCsv = "Customer Name,Street1,City,Province,PostalCode\r\n\"Acme Robotics\",100 King St,Montreal,QC,H3B 2Y5\r\n";
        var summaryC = _exportService.ImportSageBaselineFile(customerCsv, _config, syncSuppliersToDatabase: false);
        Assert.Equal(1, summaryC.CustomersImported);
        Assert.Equal("Acme Robotics", _config.CustomerNameMappings["Acme Robotics Inc."]);

        var vendorCsv = "Vendor Name,Street1,City,Province,PostalCode,Phone,Email\r\n\"Hydro-Québec\",75 Boul René-Lévesque,Montréal,QC,H2Z 1A4,514-555-1234,factures@hydro.qc.ca\r\n";
        var summaryV = _exportService.ImportSageBaselineFile(vendorCsv, _config, syncSuppliersToDatabase: true);
        Assert.Equal(1, summaryV.VendorsImported);
        Assert.Contains(_db.Suppliers.GetAll(), s => s.ContactInfo.BusinessName == "Hydro-Québec");

        var accountsCsv = "Account Number,Account Name\r\n1200,Accounts Receivable\r\n5600,Utilities\r\n";
        var summaryA = _exportService.ImportSageBaselineFile(accountsCsv, _config, syncSuppliersToDatabase: false);
        Assert.Equal(2, summaryA.AccountsImported);
    }

    [Fact]
    public void GenerateSalesInvoicesCsv_GeneratesValidCsvRows()
    {
        var project = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Web App",
            Client = new ClientInfo { BusinessName = "Tech Client" }
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = project.Id,
            InvoiceNumber = "INV-001",
            Date = new DateTime(2026, 3, 1),
            DueDate = new DateTime(2026, 3, 31),
            LineItems = [new InvoiceLine { Description = "Development", Quantity = 1, UnitPrice = 200m }]
        };

        var csv = _exportService.GenerateSalesInvoicesCsv([invoice], _config);

        Assert.Contains("InvoiceNumber,Date,DueDate,ClientName", csv);
        Assert.Contains("INV-001", csv);
        Assert.Contains("Tech Client", csv);
        Assert.Contains("200.00", csv);
    }

    [Fact]
    public void GenerateSage50Imp_StrictPennyReconciliation_BalancesExactly()
    {
        var bill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = "BILL-PENNY-1",
            SupplierName = "Testing Vendor",
            Date = new DateTime(2026, 4, 10),
            DueDate = new DateTime(2026, 5, 10),
            Amount = 100.01m,
            SubTotal = 86.98m,
            TpsAmount = 4.35m,
            TvqAmount = 8.68m,
            LineItems =
            [
                new BillLineItem { Description = "Item 1", Quantity = 1, UnitPrice = 28.99m },
                new BillLineItem { Description = "Item 2", Quantity = 1, UnitPrice = 28.99m },
                new BillLineItem { Description = "Item 3", Quantity = 1, UnitPrice = 29.00m }
            ]
        };
        _db.Bills.Save(bill);

        var imp = _exportService.GenerateSage50Imp([], [bill], _config);

        Assert.NotNull(imp);
        Assert.Contains("<PurInvoice>", imp);
        Assert.Contains("\"3\", \"\", \"BILL-PENNY-1\", \"04-10-2026\", \"100.01\", \"0.00\", \"0\", \"30\", \"0.00\"", imp);
    }

    [Fact]
    public void GenerateSage50Imp_FieldTruncationLimits_EnforcesSage50MaxLengths()
    {
        var bill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Very Long Supplier Corporate Solutions North America Ltd",
            InvoiceNumber = "INV-2026-LONG-ALPHA-NUMERIC-001",
            Date = new DateTime(2026, 4, 10),
            Amount = 150.00m,
            SubTotal = 130.46m,
            TpsAmount = 6.52m,
            TvqAmount = 13.02m
        };
        _db.Bills.Save(bill);

        var imp = _exportService.GenerateSage50Imp([], [bill], _config);

        Assert.NotNull(imp);
        var expectedSupplier = "Very Long Supplier Corporate Soluti";
        Assert.Contains($"\"{expectedSupplier}\"", imp);

        var expectedInvoice = "INV-2026-LONG-ALPHA-";
        Assert.Contains($"\"{expectedInvoice}\"", imp);
    }

    [Fact]
    public void GenerateSage50Imp_CreditNote_GeneratesNegativeAmounts()
    {
        var creditBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = "CN-9921",
            SupplierName = "Parts Supplier",
            Date = new DateTime(2026, 4, 12),
            Amount = 114.98m,
            SubTotal = 100.00m,
            TpsAmount = 5.00m,
            TvqAmount = 9.98m,
            IsCreditNote = true
        };
        _db.Bills.Save(creditBill);

        var imp = _exportService.GenerateSage50Imp([], [creditBill], _config);

        Assert.NotNull(imp);
        Assert.Contains("\"-114.98\"", imp);
        Assert.Contains("\"-100.00\"", imp);
        Assert.Contains("\"-5.00\"", imp);
        Assert.Contains("\"-9.98\"", imp);
    }
}
