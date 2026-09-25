using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Tests;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class AccountingServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly FinancialCalculationEngine _calcEngine;
    private readonly AccountingService _service;

    public AccountingServiceTests()
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
        _calcEngine = new FinancialCalculationEngine(_db.Invoices, _db.Quotes, _db.Projects);
        _service = new AccountingService(_db, _calcEngine);

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

    #region Constructor

    [Fact]
    public void Constructor_WithValidDependencies_InitializesCorrectly()
    {
        Assert.NotNull(_service);
    }

    #endregion

    #region GetProjectGrossMargin - Revenue

    [Fact]
    public void GetProjectGrossMargin_RevenueFromNonVoidInvoices_IgnoresVoidInvoices()
    {
        const string projectId = "proj-rev-invoices";

        // Non-void invoices for the target project
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-1",
            ProjectId = projectId,
            Status = "Paid",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 2, UnitPrice = 500m } // SubTotal = 1000
            }
        });

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-2",
            ProjectId = projectId,
            Status = "Final",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 750m } // SubTotal = 750
            }
        });

        // Void invoice for target project (must be ignored)
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-void",
            ProjectId = projectId,
            Status = "Void",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 10000m }
            }
        });

        // Invoice for a different project (must be ignored)
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-other",
            ProjectId = "other-project",
            Status = "Paid",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 5000m }
            }
        });

        // Accepted quote for target project (should be ignored because non-void invoices exist)
        _db.Quotes.Save(new Quote
        {
            Id = "quote-ignored",
            ProjectId = projectId,
            Status = "Accepted",
            ItemRows = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 3000m }
            }
        });

        var grossMargin = _service.GetProjectGrossMargin(projectId);

        // Revenue = 1000 + 750 = 1750, Purchases = 0, Expenses = 0
        Assert.Equal(1750m, grossMargin);
    }

    [Fact]
    public void GetProjectGrossMargin_RevenueFallbackToAcceptedQuotes_WhenNoNonVoidInvoicesExist()
    {
        const string projectId = "proj-quote-fallback";

        // A Void invoice exists, but no non-void invoices exist
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-void-only",
            ProjectId = projectId,
            Status = "Void",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 9999m }
            }
        });

        // Accepted quote for target project
        _db.Quotes.Save(new Quote
        {
            Id = "quote-accepted-1",
            ProjectId = projectId,
            Status = "Accepted",
            ItemRows = new List<QuoteItemRow>
            {
                new() { Quantity = 2, UnitPrice = 1500m } // 3000
            }
        });

        // Draft quote (must be ignored)
        _db.Quotes.Save(new Quote
        {
            Id = "quote-draft",
            ProjectId = projectId,
            Status = "Draft",
            ItemRows = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 2000m }
            }
        });

        // Rejected quote (must be ignored)
        _db.Quotes.Save(new Quote
        {
            Id = "quote-rejected",
            ProjectId = projectId,
            Status = "Rejected",
            ItemRows = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 5000m }
            }
        });

        // Accepted quote for another project (must be ignored)
        _db.Quotes.Save(new Quote
        {
            Id = "quote-other-proj",
            ProjectId = "different-proj",
            Status = "Accepted",
            ItemRows = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 4000m }
            }
        });

        var grossMargin = _service.GetProjectGrossMargin(projectId);

        // Fallback revenue = 3000, Purchases = 0, Expenses = 0
        Assert.Equal(3000m, grossMargin);
    }

    [Fact]
    public void GetProjectGrossMargin_RevenueIsZero_WhenNeitherInvoicesNorAcceptedQuotesExist()
    {
        const string projectId = "proj-zero-revenue";

        // Only void invoice
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-void",
            ProjectId = projectId,
            Status = "Void",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 1000m }
            }
        });

        // Only Draft and Rejected quotes
        _db.Quotes.Save(new Quote
        {
            Id = "quote-draft",
            ProjectId = projectId,
            Status = "Draft",
            ItemRows = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 2500m }
            }
        });

        _db.Quotes.Save(new Quote
        {
            Id = "quote-rejected",
            ProjectId = projectId,
            Status = "Rejected",
            ItemRows = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 3500m }
            }
        });

        var grossMargin = _service.GetProjectGrossMargin(projectId);

        Assert.Equal(0m, grossMargin);
    }

    [Fact]
    public void GetProjectGrossMargin_WithEmptyDatabase_ReturnsZero()
    {
        var grossMargin = _service.GetProjectGrossMargin("untracked-project-id");

        Assert.Equal(0m, grossMargin);
    }

    #endregion

    #region GetProjectGrossMargin - Deductions

    [Fact]
    public void GetProjectGrossMargin_DeductionsForPurchases_DeductsNonVoidTargetProjectItemsOnly()
    {
        const string projectId = "proj-purchases";

        // Base revenue via invoice
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-base",
            ProjectId = projectId,
            Status = "Final",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 5000m }
            }
        });

        // Non-void purchase order containing items for target project and another project
        _db.Purchases.Save(new PurchaseOrder
        {
            Id = "po-valid",
            Status = "Approved",
            Items = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 2, UnitPrice = 200m }, // 400m
                new() { ProjectId = "other-proj", Quantity = 1, UnitPrice = 300m } // Ignored
            }
        });

        // Void purchase order containing item for target project (must be ignored)
        _db.Purchases.Save(new PurchaseOrder
        {
            Id = "po-void",
            Status = "Void",
            Items = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 1, UnitPrice = 1000m } // Ignored
            }
        });

        // Non-void purchase order with no items for target project
        _db.Purchases.Save(new PurchaseOrder
        {
            Id = "po-other",
            Status = "Ordered",
            Items = new List<PoItem>
            {
                new() { ProjectId = "other-proj", Quantity = 1, UnitPrice = 500m }
            }
        });

        var grossMargin = _service.GetProjectGrossMargin(projectId);

        // Revenue = 5000, Purchases = 400, Expenses = 0 -> 4600
        Assert.Equal(4600m, grossMargin);
    }

    [Fact]
    public void GetProjectGrossMargin_DeductionsForExpenses_DeductsTargetProjectItemsOnly()
    {
        const string projectId = "proj-expenses";

        // Base revenue via invoice
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-base",
            ProjectId = projectId,
            Status = "Paid",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 4000m }
            }
        });

        // Expense report with item for target project and item for another project
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-1",
            Items = new List<ExpenseItem>
            {
                new() { ProjectId = projectId, Amount = 250m, IsKilometrage = false }, // 250m
                new() { ProjectId = "other-proj", Amount = 500m, IsKilometrage = false } // Ignored
            }
        });

        // Second expense report with kilometrage item for target project
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-2",
            Items = new List<ExpenseItem>
            {
                new() { ProjectId = projectId, IsKilometrage = true, Kilometers = 120m, KilometrageRate = 0.50m } // 60m
            }
        });

        var grossMargin = _service.GetProjectGrossMargin(projectId);

        // Revenue = 4000, Purchases = 0, Expenses = 250 + 60 = 310 -> 3690
        Assert.Equal(3690m, grossMargin);
    }

    [Fact]
    public void GetProjectGrossMargin_NetGrossMarginCalculation_ComputesTotalIncomeMinusPurchasesAndExpenses()
    {
        const string projectId = "proj-net-margin";

        // Invoice revenue: 10,000
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-10k",
            ProjectId = projectId,
            Status = "Final",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 10000m }
            }
        });

        // Purchases: 2 items = 1500 + 500 = 2000
        _db.Purchases.Save(new PurchaseOrder
        {
            Id = "po-net",
            Status = "Received",
            Items = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 3, UnitPrice = 500m }, // 1500
                new() { ProjectId = projectId, Quantity = 1, UnitPrice = 500m }  // 500
            }
        });

        // Expenses: 1200 + 300 = 1500
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-net",
            Items = new List<ExpenseItem>
            {
                new() { ProjectId = projectId, Amount = 1200m },
                new() { ProjectId = projectId, Amount = 300m }
            }
        });

        var grossMargin = _service.GetProjectGrossMargin(projectId);

        // Revenue: 10000 - (Purchases: 2000 + Expenses: 1500) = 6500
        Assert.Equal(6500m, grossMargin);
    }

    [Fact]
    public void GetProjectGrossMargin_WhenCostsExceedIncome_ReturnsNegativeMargin()
    {
        const string projectId = "proj-negative";

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-low",
            ProjectId = projectId,
            Status = "Paid",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 1000m }
            }
        });

        _db.Purchases.Save(new PurchaseOrder
        {
            Id = "po-high",
            Status = "Ordered",
            Items = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 1, UnitPrice = 1500m }
            }
        });

        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-high",
            Items = new List<ExpenseItem>
            {
                new() { ProjectId = projectId, Amount = 700m }
            }
        });

        var grossMargin = _service.GetProjectGrossMargin(projectId);

        // 1000 - (1500 + 700) = -1200
        Assert.Equal(-1200m, grossMargin);
    }

    #endregion

    #region ProcessInvoiceStatusChange

    [Fact]
    public void ProcessInvoiceStatusChange_DraftToFinal_CreatesTimelineNote()
    {
        const string projectId = "proj-status-draft-final";
        _db.Projects.Save(new Project { Id = projectId, Name = "Test Project" });

        var invoice = new Invoice
        {
            Id = "inv-dtf",
            ProjectId = projectId,
            InvoiceNumber = "INV-001",
            Status = "Final"
        };
        _db.Invoices.Save(invoice);

        _service.ProcessInvoiceStatusChange(invoice, "Draft", "Final", "TestUser");

        var notes = _db.ProjectNotes.GetByProject(projectId);
        var note = notes.FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Final_{invoice.Id}");

        Assert.NotNull(note);
        Assert.Contains("Finalized", note.Title);
        Assert.Equal("TestUser", note.CreatedBy);
        Assert.True(note.IsSystemEvent);
    }

    [Fact]
    public void ProcessInvoiceStatusChange_FinalToDraft_RemovesTimelineNote()
    {
        const string projectId = "proj-status-final-draft";
        _db.Projects.Save(new Project { Id = projectId, Name = "Test Project" });

        var invoice = new Invoice
        {
            Id = "inv-ftd",
            ProjectId = projectId,
            InvoiceNumber = "INV-002",
            Status = "Draft"
        };
        _db.Invoices.Save(invoice);

        // First transition to Final to establish the note
        _service.ProcessInvoiceStatusChange(invoice, "Draft", "Final", "User1");
        Assert.NotNull(_db.ProjectNotes.GetByProject(projectId).FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Final_{invoice.Id}"));

        // Revert to Draft
        _service.ProcessInvoiceStatusChange(invoice, "Final", "Draft", "User1");
        Assert.Null(_db.ProjectNotes.GetByProject(projectId).FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Final_{invoice.Id}"));
    }

    [Fact]
    public void ProcessInvoiceStatusChange_FinalToPaid_CreatesPaidTimelineNoteAndEvaluatesCommissions()
    {
        const string projectId = "proj-status-paid";
        var project = new Project
        {
            Id = projectId,
            Name = "Commissions Project",
            Status = "Completed",
            Commissions = new List<CommissionBeneficiary>
            {
                new()
                {
                    Name = "Alice Sales",
                    Percentage = 10m,
                    CommissionBasis = CommissionBasisTypes.Revenue
                }
            }
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv-paid-1",
            ProjectId = projectId,
            InvoiceNumber = "INV-PAID",
            Status = "Paid",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 8000m }
            }
        };
        _db.Invoices.Save(invoice);

        _service.ProcessInvoiceStatusChange(invoice, "Final", "Paid", "FinanceUser");

        // Verify paid timeline note was created
        var notes = _db.ProjectNotes.GetByProject(projectId);
        var note = notes.FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Paid_{invoice.Id}");
        Assert.NotNull(note);
        Assert.Contains("Paid", note.Title);

        // Verify commissions evaluated cleanly
        var ledgerRecords = _db.CommissionLedgerRecords.GetByProject(projectId);
        Assert.Single(ledgerRecords);
        var record = ledgerRecords[0];
        Assert.Equal("Alice Sales", record.BeneficiaryName);
        Assert.Equal(8000m, record.BaseAmount);
        Assert.Equal(800m, record.CommissionAmount);
    }

    [Fact]
    public void ProcessInvoiceStatusChange_PaidToOtherStatus_RemovesPaidTimelineNote()
    {
        const string projectId = "proj-status-unpay";
        _db.Projects.Save(new Project { Id = projectId, Name = "Test Project" });

        var invoice = new Invoice
        {
            Id = "inv-unpay",
            ProjectId = projectId,
            InvoiceNumber = "INV-UNPAY",
            Status = "Final"
        };
        _db.Invoices.Save(invoice);

        // Mark as paid first
        _service.ProcessInvoiceStatusChange(invoice, "Final", "Paid", "User1");
        Assert.NotNull(_db.ProjectNotes.GetByProject(projectId).FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Paid_{invoice.Id}"));

        // Change from Paid back to Final
        _service.ProcessInvoiceStatusChange(invoice, "Paid", "Final", "User1");
        Assert.Null(_db.ProjectNotes.GetByProject(projectId).FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Paid_{invoice.Id}"));
    }

    [Fact]
    public void ProcessInvoiceStatusChange_WhenOldStatusEqualsNewStatus_DoesNothing()
    {
        const string projectId = "proj-same-status";
        _db.Projects.Save(new Project { Id = projectId, Name = "Same Status Project" });

        var invoice = new Invoice
        {
            Id = "inv-same",
            ProjectId = projectId,
            InvoiceNumber = "INV-SAME",
            Status = "Draft"
        };
        _db.Invoices.Save(invoice);

        _service.ProcessInvoiceStatusChange(invoice, "Draft", "Draft", "User1");

        var notes = _db.ProjectNotes.GetByProject(projectId);
        Assert.Empty(notes);
    }

    [Fact]
    public void ProcessInvoiceStatusChange_WithNullInvoiceOrEmptyProjectId_ExecutesWithoutThrowing()
    {
        // Null invoice
        var exception1 = Record.Exception(() => _service.ProcessInvoiceStatusChange(null!, "Draft", "Final", "User"));
        Assert.Null(exception1);

        // Invoice with empty ProjectId
        var invoiceEmptyProj = new Invoice { ProjectId = string.Empty, Status = "Final" };
        var exception2 = Record.Exception(() => _service.ProcessInvoiceStatusChange(invoiceEmptyProj, "Draft", "Final", "User"));
        Assert.Null(exception2);
    }

    #endregion

    #region EvaluateProjectCommissions

    [Fact]
    public void EvaluateProjectCommissions_WhenProjectDoesNotExist_ExecutesCleanlyWithoutThrowing()
    {
        var exception = Record.Exception(() => _service.EvaluateProjectCommissions("non-existent-proj"));

        Assert.Null(exception);
    }

    [Fact]
    public void EvaluateProjectCommissions_WhenProjectHasNoCommissions_ExecutesCleanlyWithoutThrowing()
    {
        const string projectId = "proj-no-comm";
        _db.Projects.Save(new Project { Id = projectId, Name = "No Commissions", Status = "Completed" });

        var exception = Record.Exception(() => _service.EvaluateProjectCommissions(projectId));

        Assert.Null(exception);
        var ledgerRecords = _db.CommissionLedgerRecords.GetByProject(projectId);
        Assert.Empty(ledgerRecords);
    }

    [Fact]
    public void EvaluateProjectCommissions_WhenProjectCompletedAndInvoicesPaid_GeneratesCommissionLedgerRecords()
    {
        const string projectId = "proj-comm-revenue";
        var project = new Project
        {
            Id = projectId,
            Name = "Revenue Comm Project",
            Status = "Completed",
            Commissions = new List<CommissionBeneficiary>
            {
                new()
                {
                    Name = "Bob Agent",
                    Role = "Sales",
                    Percentage = 15m,
                    CommissionBasis = CommissionBasisTypes.Revenue
                }
            }
        };
        _db.Projects.Save(project);

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-comm-1",
            ProjectId = projectId,
            Status = "Paid",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 20000m }
            }
        });

        _service.EvaluateProjectCommissions(projectId);

        var records = _db.CommissionLedgerRecords.GetByProject(projectId);
        Assert.Single(records);
        var record = records[0];
        Assert.Equal("Bob Agent", record.BeneficiaryName);
        Assert.Equal(20000m, record.BaseAmount);
        Assert.Equal(3000m, record.CommissionAmount); // 15% of 20000
        Assert.Equal(CommissionStatus.Payable, record.Status);
    }

    [Fact]
    public void EvaluateProjectCommissions_WithMarginBasis_CalculatesFromGrossMargin()
    {
        const string projectId = "proj-comm-margin";
        var project = new Project
        {
            Id = projectId,
            Name = "Margin Comm Project",
            Status = "Completed",
            Commissions = new List<CommissionBeneficiary>
            {
                new()
                {
                    Name = "Carol Consultant",
                    Role = "Consultant",
                    Percentage = 25m,
                    CommissionBasis = CommissionBasisTypes.Margin
                }
            }
        };
        _db.Projects.Save(project);

        // Paid invoice: 12,000
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-margin",
            ProjectId = projectId,
            Status = "Paid",
            Lines = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 12000m }
            }
        });

        // Purchase order: 4,000
        _db.Purchases.Save(new PurchaseOrder
        {
            Id = "po-margin",
            Status = "Received",
            Items = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 1, UnitPrice = 4000m }
            }
        });

        // Expense report: 2,000
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-margin",
            Items = new List<ExpenseItem>
            {
                new() { ProjectId = projectId, Amount = 2000m }
            }
        });

        // Margin = 12000 - (4000 + 2000) = 6000
        _service.EvaluateProjectCommissions(projectId);

        var records = _db.CommissionLedgerRecords.GetByProject(projectId);
        Assert.Single(records);
        var record = records[0];
        Assert.Equal("Carol Consultant", record.BeneficiaryName);
        Assert.Equal(6000m, record.BaseAmount);
        Assert.Equal(1500m, record.CommissionAmount); // 25% of 6000
        Assert.Equal(CommissionStatus.Payable, record.Status);
    }

    [Fact]
    public void EvaluateProjectCommissions_WhenNotCompletedOrInvoicesUnpaid_CleansUpPayableRecords()
    {
        const string projectId = "proj-comm-revert";
        var project = new Project
        {
            Id = projectId,
            Name = "Incomplete Project",
            Status = "Active", // Not Completed
            Commissions = new List<CommissionBeneficiary>
            {
                new() { Name = "Dan", Percentage = 10m, CommissionBasis = CommissionBasisTypes.Revenue }
            }
        };
        _db.Projects.Save(project);

        // Pre-existing payable commission record from earlier state
        var existingRecord = new CommissionLedgerRecord
        {
            Id = "ledger-pre",
            ProjectId = projectId,
            BeneficiaryName = "Dan",
            BaseAmount = 5000m,
            CommissionAmount = 500m,
            Status = CommissionStatus.Payable
        };
        _db.CommissionLedgerRecords.Save(existingRecord);

        // Evaluating commissions when project is active should remove payable records
        _service.EvaluateProjectCommissions(projectId);

        var records = _db.CommissionLedgerRecords.GetByProject(projectId);
        Assert.Empty(records);
    }

    #endregion
}
