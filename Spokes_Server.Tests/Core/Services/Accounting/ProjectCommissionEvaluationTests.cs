using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Tests;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class ProjectCommissionEvaluationTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly AccountingService _accountingService;

    public ProjectCommissionEvaluationTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _accountingService = new AccountingService(_db.AccountingContext, _db.ProjectsContext, _db.CoreContext);

        // Ensure CompanyProfile has proper system mappings for completion
        var profile = _db.CompanyProfile.Get();
        var completedStatus = profile.ProjectStatuses.FirstOrDefault(s => s.Name == ProjectStatus.Completed);
        if (completedStatus != null)
        {
            completedStatus.SystemMapping = SystemStatusMappings.Completed;
        }
        var archivedStatus = profile.ProjectStatuses.FirstOrDefault(s => s.Name == ProjectStatus.Archived);
        if (archivedStatus != null)
        {
            archivedStatus.SystemMapping = SystemStatusMappings.Archived;
        }
        _db.CompanyProfile.Save(profile);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void EvaluateProjectCommissions_GeneratesPayableLedgers_WhenProjectCompletedAndInvoicesPaid()
    {
        // Arrange
        var project = new Project
        {
            Id = "proj_comm_1",
            Name = "Commission Proj",
            Status = "Completed",
            Commissions =
            [
                new CommissionBeneficiary
                {
                    Name = "Alice",
                    EmployeeId = "emp_alice",
                    Percentage = 5.0m,
                    CommissionBasis = CommissionBasisTypes.Revenue,
                    Type = "Internal"
                }
            ]
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv_comm_1",
            ProjectId = project.Id,
            InvoiceNumber = "INV-C1",
            Status = "Paid",
            Lines =
            [
                new InvoiceLine { Description = "Services", Quantity = 1, UnitPrice = 1000m }
            ]
        };
        _db.Invoices.Save(invoice);

        // Act
        _accountingService.EvaluateProjectCommissions(project.Id);

        // Assert
        var ledgers = _db.CommissionLedgerRecords.GetByProject(project.Id);
        Assert.Single(ledgers);

        var ledger = ledgers[0];
        Assert.Equal(project.Id, ledger.ProjectId);
        Assert.Equal(CommissionStatus.Payable, ledger.Status);
        Assert.Equal(1000m, ledger.BaseAmount);
        Assert.Equal(50m, ledger.CommissionAmount);
        Assert.Equal("emp_alice", ledger.BeneficiaryId);
        Assert.Equal("Alice", ledger.BeneficiaryName);
        Assert.Equal("Internal", ledger.Type);
        Assert.Equal(DateTime.Today, ledger.DateGenerated.Date);
    }

    [Fact]
    public void EvaluateProjectCommissions_WithMarginBasis_CalculatesGrossMargin()
    {
        // Arrange
        var project = new Project
        {
            Id = "proj_margin_1",
            Name = "Margin Proj",
            Status = "Completed",
            Commissions =
            [
                new CommissionBeneficiary
                {
                    Name = "Bob",
                    EmployeeId = "emp_bob",
                    Percentage = 10.0m,
                    CommissionBasis = CommissionBasisTypes.Margin,
                    Type = "Internal"
                }
            ]
        };
        _db.Projects.Save(project);

        // Income: Paid invoice of 1000
        var invoice = new Invoice
        {
            Id = "inv_m1",
            ProjectId = project.Id,
            InvoiceNumber = "INV-M1",
            Status = "Paid",
            Lines =
            [
                new InvoiceLine { Description = "Work", Quantity = 1, UnitPrice = 1000m }
            ]
        };
        _db.Invoices.Save(invoice);

        // Purchases: PO with item linked to project totaling 200
        var po = new PurchaseOrder
        {
            Id = "po_m1",
            PoNumber = "PO-M1",
            Items =
            [
                new PoItem { ProjectId = project.Id, Quantity = 2, UnitPrice = 100m }
            ]
        };
        _db.PurchaseOrders.Save(po);

        // Expenses: Expense report with item linked to project totaling 100
        var expense = new ExpenseReport
        {
            Id = "exp_m1",
            ReportNumber = "EXP-M1",
            Items =
            [
                new ExpenseItem { ProjectId = project.Id, Amount = 100m }
            ]
        };
        _db.ExpenseReports.Save(expense);

        // Total Income = 1000
        // Total Purchases = 200
        // Total Expenses = 100
        // Gross Margin = 1000 - 300 = 700
        // Commission = 700 * 10% = 70

        // Act
        _accountingService.EvaluateProjectCommissions(project.Id);

        // Assert
        var ledgers = _db.CommissionLedgerRecords.GetByProject(project.Id);
        Assert.Single(ledgers);

        var ledger = ledgers[0];
        Assert.Equal(CommissionStatus.Payable, ledger.Status);
        Assert.Equal(700m, ledger.BaseAmount);
        Assert.Equal(70m, ledger.CommissionAmount);
        Assert.Equal("emp_bob", ledger.BeneficiaryId);
        Assert.Equal("Bob", ledger.BeneficiaryName);
    }

    [Fact]
    public void EvaluateProjectCommissions_WhenUncompletedOrUnpaid_ClawbackPaidAndDeletesPayable()
    {
        // Arrange: Project is in "Draft" status (not completed)
        var project = new Project
        {
            Id = "proj_clawback_1",
            Name = "Clawback Proj",
            Status = "Draft",
            Commissions =
            [
                new CommissionBeneficiary
                {
                    Name = "Charlie",
                    EmployeeId = "emp_charlie",
                    Percentage = 5.0m,
                    CommissionBasis = CommissionBasisTypes.Revenue,
                    Type = "Internal"
                }
            ]
        };
        _db.Projects.Save(project);

        // Existing ledgers: 1 Paid, 1 Payable, 1 Pending
        var paidLedger = new CommissionLedgerRecord
        {
            Id = "ledger_paid",
            ProjectId = project.Id,
            BeneficiaryId = "emp_charlie",
            BeneficiaryName = "Charlie",
            Type = "Internal",
            BaseAmount = 1000m,
            CommissionAmount = 50m,
            Status = CommissionStatus.Paid
        };
        _db.CommissionLedgerRecords.Save(paidLedger);

        var payableLedger = new CommissionLedgerRecord
        {
            Id = "ledger_payable",
            ProjectId = project.Id,
            BeneficiaryId = "emp_charlie",
            BeneficiaryName = "Charlie",
            Type = "Internal",
            BaseAmount = 1000m,
            CommissionAmount = 50m,
            Status = CommissionStatus.Payable
        };
        _db.CommissionLedgerRecords.Save(payableLedger);

        var pendingLedger = new CommissionLedgerRecord
        {
            Id = "ledger_pending",
            ProjectId = project.Id,
            BeneficiaryId = "emp_charlie",
            BeneficiaryName = "Charlie",
            Type = "Internal",
            BaseAmount = 1000m,
            CommissionAmount = 50m,
            Status = CommissionStatus.Pending
        };
        _db.CommissionLedgerRecords.Save(pendingLedger);

        // Act
        _accountingService.EvaluateProjectCommissions(project.Id);

        // Assert:
        // 1. Payable and Pending records deleted
        var remainingPayable = _db.CommissionLedgerRecords.GetById("ledger_payable");
        var remainingPending = _db.CommissionLedgerRecords.GetById("ledger_pending");
        Assert.Null(remainingPayable);
        Assert.Null(remainingPending);

        // 2. Paid record untouched
        var remainingPaid = _db.CommissionLedgerRecords.GetById("ledger_paid");
        Assert.NotNull(remainingPaid);
        Assert.Equal(CommissionStatus.Paid, remainingPaid.Status);

        // 3. New Clawback record created with negative amounts
        var allRecords = _db.CommissionLedgerRecords.GetByProject(project.Id);
        var clawback = allRecords.FirstOrDefault(r => r.Id != "ledger_paid");
        Assert.NotNull(clawback);
        Assert.Equal(-1000m, clawback.BaseAmount);
        Assert.Equal(-50m, clawback.CommissionAmount);
        Assert.Equal("Charlie", clawback.BeneficiaryName);
        Assert.Equal("emp_charlie", clawback.BeneficiaryId);
    }

    [Fact]
    public void EvaluateProjectCommissions_WhenProjectNotFoundOrNoCommissions_DoesNothing()
    {
        // Null project
        _accountingService.EvaluateProjectCommissions("non_existent_project");
        Assert.Empty(_db.CommissionLedgerRecords.GetAll());

        // Project without commissions
        var project = new Project
        {
            Id = "proj_no_comm",
            Name = "No Comm",
            Status = "Completed",
            Commissions = []
        };
        _db.Projects.Save(project);

        _accountingService.EvaluateProjectCommissions(project.Id);
        Assert.Empty(_db.CommissionLedgerRecords.GetByProject(project.Id));
    }

    [Fact]
    public void EvaluateProjectCommissions_WhenExistingLedgersPresentAndNotClawback_DoesNotDuplicate()
    {
        // Arrange: Completed project, paid invoice, but already has a Payable ledger
        var project = new Project
        {
            Id = "proj_no_dup",
            Name = "No Duplicate Proj",
            Status = "Completed",
            Commissions =
            [
                new CommissionBeneficiary
                {
                    Name = "David",
                    EmployeeId = "emp_david",
                    Percentage = 5.0m
                }
            ]
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv_dup_1",
            ProjectId = project.Id,
            Status = "Paid",
            Lines = [new InvoiceLine { Quantity = 1, UnitPrice = 1000m }]
        };
        _db.Invoices.Save(invoice);

        var existing = new CommissionLedgerRecord
        {
            Id = "existing_ledger",
            ProjectId = project.Id,
            BeneficiaryName = "David",
            Status = CommissionStatus.Payable,
            BaseAmount = 1000m,
            CommissionAmount = 50m
        };
        _db.CommissionLedgerRecords.Save(existing);

        // Act
        _accountingService.EvaluateProjectCommissions(project.Id);

        // Assert: Still only 1 record
        var records = _db.CommissionLedgerRecords.GetByProject(project.Id);
        Assert.Single(records);
        Assert.Equal("existing_ledger", records[0].Id);
    }

    [Fact]
    public void EvaluateProjectCommissions_WhenNoInvoices_UsesAcceptedQuoteRevenue()
    {
        // Arrange: Completed project with NO invoices, but with an Accepted quote
        var project = new Project
        {
            Id = "proj_quote_rev",
            Name = "Quote Revenue Proj",
            Status = "Completed",
            Commissions =
            [
                new CommissionBeneficiary
                {
                    Name = "Emma",
                    EmployeeId = "emp_emma",
                    Percentage = 10.0m,
                    CommissionBasis = CommissionBasisTypes.Revenue
                }
            ]
        };
        _db.Projects.Save(project);

        var quote = new Quote
        {
            Id = "quote_acc",
            ProjectId = project.Id,
            Status = "Accepted",
            LaborItems =
            [
                new QuoteLaborItem { Hours = 5, Rate = 200m } // Total = 1000
            ]
        };
        _db.Quotes.Save(quote);

        // Act
        _accountingService.EvaluateProjectCommissions(project.Id);

        // Assert: Commission based on Accepted quote GrandTotal (1000)
        var ledgers = _db.CommissionLedgerRecords.GetByProject(project.Id);
        Assert.Single(ledgers);
        Assert.Equal(1000m, ledgers[0].BaseAmount);
        Assert.Equal(100m, ledgers[0].CommissionAmount);
    }

    [Fact]
    public void EvaluateProjectCommissions_WhenBaseAmountZeroOrNegative_SkipsRecord()
    {
        // Arrange: Purchases exceed revenue, leading to negative margin
        var project = new Project
        {
            Id = "proj_neg_margin",
            Name = "Negative Margin Proj",
            Status = "Completed",
            Commissions =
            [
                new CommissionBeneficiary
                {
                    Name = "Frank",
                    Percentage = 10.0m,
                    CommissionBasis = CommissionBasisTypes.Margin
                }
            ]
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv_neg",
            ProjectId = project.Id,
            Status = "Paid",
            Lines = [new InvoiceLine { Quantity = 1, UnitPrice = 100m }]
        };
        _db.Invoices.Save(invoice);

        var po = new PurchaseOrder
        {
            Id = "po_neg",
            Items = [new PoItem { ProjectId = project.Id, Quantity = 1, UnitPrice = 500m }]
        };
        _db.PurchaseOrders.Save(po);

        // Margin = 100 - 500 = -400 <= 0

        // Act
        _accountingService.EvaluateProjectCommissions(project.Id);

        // Assert: No ledger generated
        var ledgers = _db.CommissionLedgerRecords.GetByProject(project.Id);
        Assert.Empty(ledgers);
    }

    [Fact]
    public void EvaluateProjectCommissions_ClawbackAlreadyExists_DoesNotCreateDuplicateClawback()
    {
        // Arrange: Project uncompleted, has Paid ledger AND already has a Clawback ledger
        var project = new Project
        {
            Id = "proj_clawback_dup",
            Name = "Clawback Dup Proj",
            Status = "Draft",
            Commissions =
            [
                new CommissionBeneficiary { Name = "Grace", Percentage = 5m }
            ]
        };
        _db.Projects.Save(project);

        var paidLedger = new CommissionLedgerRecord
        {
            Id = "paid_l",
            ProjectId = project.Id,
            BeneficiaryName = "Grace",
            BaseAmount = 1000m,
            CommissionAmount = 50m,
            Status = CommissionStatus.Paid
        };
        _db.CommissionLedgerRecords.Save(paidLedger);

        var clawbackLedger = new CommissionLedgerRecord
        {
            Id = "clawback_l",
            ProjectId = project.Id,
            BeneficiaryName = "Grace",
            BaseAmount = -1000m,
            CommissionAmount = -50m,
            Status = CommissionStatus.Clawback
        };
        _db.CommissionLedgerRecords.Save(clawbackLedger);

        // Act
        _accountingService.EvaluateProjectCommissions(project.Id);

        // Assert: No new clawback created
        var ledgers = _db.CommissionLedgerRecords.GetByProject(project.Id);
        Assert.Equal(2, ledgers.Count);
    }

    [Fact]
    public void EvaluateProjectCommissions_WithCompanyProfileStatusConfigMapping_EvaluatesCompleted()
    {
        // Arrange: Custom status "Delivered" mapped to Completed in CompanyProfile
        var profile = _db.CompanyProfile.Get();
        profile.ProjectStatuses.Add(new ProjectStatusConfig
        {
            Name = "Delivered",
            SystemMapping = SystemStatusMappings.Completed
        });
        _db.CompanyProfile.Save(profile);

        var project = new Project
        {
            Id = "proj_custom_status",
            Name = "Custom Status Proj",
            Status = "Delivered",
            Commissions =
            [
                new CommissionBeneficiary { Name = "Henry", Percentage = 10m }
            ]
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv_custom_s",
            ProjectId = project.Id,
            Status = "Paid",
            Lines = [new InvoiceLine { Quantity = 1, UnitPrice = 500m }]
        };
        _db.Invoices.Save(invoice);

        // Act
        _accountingService.EvaluateProjectCommissions(project.Id);

        // Assert: Ledger generated because "Delivered" maps to Completed
        var ledgers = _db.CommissionLedgerRecords.GetByProject(project.Id);
        Assert.Single(ledgers);
        Assert.Equal(50m, ledgers[0].CommissionAmount);
    }

    [Fact]
    public void EvaluateProjectCommissions_WhenBeneficiaryHasNoEmployeeId_UsesNameAsBeneficiaryId()
    {
        // Arrange
        var project = new Project
        {
            Id = "proj_name_id",
            Name = "Name Id Proj",
            Status = "Archived", // Archived also counts as completed
            Commissions =
            [
                new CommissionBeneficiary
                {
                    Name = "External Agent",
                    EmployeeId = null, // null employee ID
                    Percentage = 5m,
                    Type = "External"
                }
            ]
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv_name_id",
            ProjectId = project.Id,
            Status = "Paid",
            Lines = [new InvoiceLine { Quantity = 1, UnitPrice = 200m }]
        };
        _db.Invoices.Save(invoice);

        // Act
        _accountingService.EvaluateProjectCommissions(project.Id);

        // Assert
        var ledgers = _db.CommissionLedgerRecords.GetByProject(project.Id);
        Assert.Single(ledgers);
        Assert.Equal("External Agent", ledgers[0].BeneficiaryId);
        Assert.Equal("External Agent", ledgers[0].BeneficiaryName);
        Assert.Equal("External", ledgers[0].Type);
        Assert.Equal(10m, ledgers[0].CommissionAmount);
    }
}
