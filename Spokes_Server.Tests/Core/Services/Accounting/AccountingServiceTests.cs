using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Tests;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class AccountingServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly AccountingService _service;
    private static readonly Employee AdminActor = new() { Id = "acct-test-admin", FirstName = "Test", LastName = "Admin", IsActive = true, IsAdmin = true };

    public AccountingServiceTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _service = new AccountingService(_db.AccountingContext, _db.ProjectsContext, _db.CoreContext);

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
            LineItems = new List<InvoiceLine>
            {
                new() { Quantity = 2, UnitPrice = 500m } // SubTotal = 1000
            }
        });

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-2",
            ProjectId = projectId,
            Status = "Final",
            LineItems = new List<InvoiceLine>
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
            LineItems = new List<InvoiceLine>
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
            LineItems = new List<InvoiceLine>
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
            MaterialLineItems = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 3000m }
            }
        });

        var grossMargin = _service.CalculateProjectGrossMargin(projectId);

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
            LineItems = new List<InvoiceLine>
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
            MaterialLineItems = new List<QuoteItemRow>
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
            MaterialLineItems = new List<QuoteItemRow>
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
            MaterialLineItems = new List<QuoteItemRow>
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
            MaterialLineItems = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 4000m }
            }
        });

        var grossMargin = _service.CalculateProjectGrossMargin(projectId);

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
            LineItems = new List<InvoiceLine>
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
            MaterialLineItems = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 2500m }
            }
        });

        _db.Quotes.Save(new Quote
        {
            Id = "quote-rejected",
            ProjectId = projectId,
            Status = "Rejected",
            MaterialLineItems = new List<QuoteItemRow>
            {
                new() { Quantity = 1, UnitPrice = 3500m }
            }
        });

        var grossMargin = _service.CalculateProjectGrossMargin(projectId);

        Assert.Equal(0m, grossMargin);
    }

    [Fact]
    public void GetProjectGrossMargin_WithEmptyDatabase_ReturnsZero()
    {
        var grossMargin = _service.CalculateProjectGrossMargin("untracked-project-id");

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
            LineItems = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 5000m }
            }
        });

        // Non-void purchase order containing items for target project and another project
        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-valid",
            Status = "Approved",
            LineItems = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 2, UnitPrice = 200m }, // 400m
                new() { ProjectId = "other-proj", Quantity = 1, UnitPrice = 300m } // Ignored
            }
        });

        // Void purchase order containing item for target project (must be ignored)
        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-void",
            Status = "Void",
            LineItems = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 1, UnitPrice = 1000m } // Ignored
            }
        });

        // Non-void purchase order with no items for target project
        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-other",
            Status = "Ordered",
            LineItems = new List<PoItem>
            {
                new() { ProjectId = "other-proj", Quantity = 1, UnitPrice = 500m }
            }
        });

        var grossMargin = _service.CalculateProjectGrossMargin(projectId);

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
            LineItems = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 4000m }
            }
        });

        // Expense report with item for target project and item for another project
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-1",
            DateSubmitted = DateTime.Today,
            LineItems = new List<ExpenseItem>
            {
                new() { ProjectId = projectId, Amount = 250m, IsKilometrage = false }, // 250m
                new() { ProjectId = "other-proj", Amount = 500m, IsKilometrage = false } // Ignored
            }
        });

        // Second expense report with kilometrage item for target project
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-2",
            DateSubmitted = DateTime.Today,
            LineItems = new List<ExpenseItem>
            {
                new() { ProjectId = projectId, IsKilometrage = true, Kilometers = 120m, KilometrageRate = 0.50m } // 60m
            }
        });

        var grossMargin = _service.CalculateProjectGrossMargin(projectId);

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
            LineItems = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 10000m }
            }
        });

        // Purchases: 2 items = 1500 + 500 = 2000
        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-net",
            Status = "Received",
            LineItems = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 3, UnitPrice = 500m }, // 1500
                new() { ProjectId = projectId, Quantity = 1, UnitPrice = 500m }  // 500
            }
        });

        // Expenses: 1200 + 300 = 1500
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-net",
            DateSubmitted = DateTime.Today,
            LineItems = new List<ExpenseItem>
            {
                new() { ProjectId = projectId, Amount = 1200m },
                new() { ProjectId = projectId, Amount = 300m }
            }
        });

        var grossMargin = _service.CalculateProjectGrossMargin(projectId);

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
            LineItems = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 1000m }
            }
        });

        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-high",
            Status = "Ordered",
            LineItems = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 1, UnitPrice = 1500m }
            }
        });

        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-high",
            DateSubmitted = DateTime.Today,
            LineItems = new List<ExpenseItem>
            {
                new() { ProjectId = projectId, Amount = 700m }
            }
        });

        var grossMargin = _service.CalculateProjectGrossMargin(projectId);

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

        _service.ProcessInvoiceStatusChange(invoice, "Draft", "Final", "TestUser", AdminActor);

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
        _service.ProcessInvoiceStatusChange(invoice, "Draft", "Final", "User1", AdminActor);
        Assert.NotNull(_db.ProjectNotes.GetByProject(projectId).FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Final_{invoice.Id}"));

        // Revert to Draft
        _service.ProcessInvoiceStatusChange(invoice, "Final", "Draft", "User1", AdminActor);
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
            LineItems = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 8000m }
            }
        };
        _db.Invoices.Save(invoice);

        _service.ProcessInvoiceStatusChange(invoice, "Final", "Paid", "FinanceUser", AdminActor);

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
        _service.ProcessInvoiceStatusChange(invoice, "Final", "Paid", "User1", AdminActor);
        Assert.NotNull(_db.ProjectNotes.GetByProject(projectId).FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Paid_{invoice.Id}"));

        // Change from Paid back to Final
        _service.ProcessInvoiceStatusChange(invoice, "Paid", "Final", "User1", AdminActor);
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

        _service.ProcessInvoiceStatusChange(invoice, "Draft", "Draft", "User1", AdminActor);

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
        var exception2 = Record.Exception(() => _service.ProcessInvoiceStatusChange(invoiceEmptyProj, "Draft", "Final", "User", AdminActor));
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
            LineItems = new List<InvoiceLine>
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
            LineItems = new List<InvoiceLine>
            {
                new() { Quantity = 1, UnitPrice = 12000m }
            }
        });

        // Purchase order: 4,000
        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-margin",
            Status = "Received",
            LineItems = new List<PoItem>
            {
                new() { ProjectId = projectId, Quantity = 1, UnitPrice = 4000m }
            }
        });

        // Expense report: 2,000
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-margin",
            DateSubmitted = DateTime.Today,
            LineItems = new List<ExpenseItem>
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

    #region Linearized Orchestration & Payment Recording

    [Fact]
    public void ProcessInvoiceStatusChange_OrchestratesCommissionsBeforeTimelineNotification()
    {
        const string projectId = "proj-linear-handoff";
        var project = new Project
        {
            Id = projectId,
            Name = "Linear Handoff Project",
            Status = "Completed",
            Commissions = new List<CommissionBeneficiary>
            {
                new() { Name = "Seller", Percentage = 10m, CommissionBasis = CommissionBasisTypes.Revenue }
            }
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv-linear-1",
            ProjectId = projectId,
            InvoiceNumber = "INV-LIN-1",
            Status = InvoiceStatus.Paid,
            LineItems = new List<InvoiceLine> { new() { Quantity = 1, UnitPrice = 5000m } }
        };
        _db.Invoices.Save(invoice);

        bool commissionsEvaluatedPriorToTimelineNotification = false;
        var mockTimeline = new Mock<Spokes_Server.Core.Services.Projects.ITimelineEventService>();
        mockTimeline
            .Setup(t => t.SyncInvoiceStatusTimelineEvent(It.IsAny<Invoice>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Invoice, string, string, string?>((inv, oldSt, newSt, usr) =>
            {
                // Verify that commission evaluation happened BEFORE timeline event was notified
                var records = _db.CommissionLedgerRecords.GetByProject(projectId);
                commissionsEvaluatedPriorToTimelineNotification = records.Count == 1 && records[0].CommissionAmount == 500m;
            });

        var orchestratedService = new AccountingService(_db.AccountingContext, _db.ProjectsContext, _db.CoreContext, timelineEventService: mockTimeline.Object);
        orchestratedService.ProcessInvoiceStatusChange(invoice, InvoiceStatus.Final, InvoiceStatus.Paid, "FinanceUser", AdminActor);

        Assert.True(commissionsEvaluatedPriorToTimelineNotification, "AccountingService must evaluate commissions before notifying ITimelineEventService.");
        mockTimeline.Verify(t => t.SyncInvoiceStatusTimelineEvent(invoice, InvoiceStatus.Final, InvoiceStatus.Paid, "FinanceUser"), Times.Once);
    }

    [Fact]
    public void RecordInvoicePayment_FullPayment_MarksPaidAndOrchestratesStatusChange()
    {
        const string projectId = "proj-rec-pay-full";
        _db.Projects.Save(new Project { Id = projectId, Name = "Payment Project" });

        var invoice = new Invoice
        {
            Id = "inv-full-pay",
            ProjectId = projectId,
            InvoiceNumber = "INV-PAY-1",
            Status = InvoiceStatus.Final,
            TaxRate = 0m,
            LineItems = new List<InvoiceLine> { new() { Quantity = 1, UnitPrice = 2500m } }
        };
        _db.Invoices.Save(invoice);

        var payment = new PaymentRecord
        {
            Amount = 2500m,
            Date = new DateTime(2026, 9, 30),
            Method = "Check",
            Reference = "CHK-9988"
        };

        _service.RecordInvoicePayment(invoice, payment, "BillingUser", AdminActor);

        // Verify entity properties mutated and saved
        var savedInvoice = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(savedInvoice);
        Assert.Equal(InvoiceStatus.Paid, savedInvoice.Status);
        Assert.Equal(new DateTime(2026, 9, 30), savedInvoice.DatePaid);
        Assert.Single(savedInvoice.Payments);
        Assert.Equal(2500m, savedInvoice.Payments[0].Amount);

        // Verify timeline event created
        var notes = _db.ProjectNotes.GetByProject(projectId);
        var paidNote = notes.FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Paid_{invoice.Id}");
        Assert.NotNull(paidNote);
        Assert.Contains("Paid", paidNote.Title);
        Assert.Equal("BillingUser", paidNote.CreatedBy);
    }

    [Fact]
    public void RecordInvoicePayment_PartialPayment_MarksPartialAndSaves()
    {
        const string projectId = "proj-rec-pay-part";
        _db.Projects.Save(new Project { Id = projectId, Name = "Partial Payment Project" });

        var invoice = new Invoice
        {
            Id = "inv-part-pay",
            ProjectId = projectId,
            InvoiceNumber = "INV-PAY-2",
            Status = InvoiceStatus.Final,
            TaxRate = 0m,
            LineItems = new List<InvoiceLine> { new() { Quantity = 1, UnitPrice = 3000m } }
        };
        _db.Invoices.Save(invoice);

        var payment = new PaymentRecord
        {
            Amount = 1000m,
            Date = new DateTime(2026, 9, 30),
            Method = "Credit Card",
            Reference = "CC-1234"
        };

        _service.RecordInvoicePayment(invoice, payment, "BillingUser", AdminActor);

        var savedInvoice = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(savedInvoice);
        Assert.Equal(InvoiceStatus.Partial, savedInvoice.Status);
        Assert.Null(savedInvoice.DatePaid);
        Assert.Equal(2000m, savedInvoice.BalanceDue);
    }

    [Fact]
    public void RecordBillPayment_FullPayment_MarksPaidAndSaves()
    {
        var bill = new Bill
        {
            Id = "bill-test-1",
            Status = BillStatus.Unpaid,
            Amount = 1200m
        };
        _db.Bills.Save(bill);

        var payment = new PaymentRecord
        {
            Amount = 1200m,
            Date = new DateTime(2026, 9, 30),
            Method = "Wire",
            Reference = "WIRE-5544"
        };

        _service.RecordBillPayment(bill, payment, actor: AdminActor);

        var savedBill = _db.Bills.GetById(bill.Id);
        Assert.NotNull(savedBill);
        Assert.Equal(BillStatus.Paid, savedBill.Status);
        Assert.Equal(new DateTime(2026, 9, 30), savedBill.DatePaid);
        Assert.Single(savedBill.Payments);
        Assert.Equal(1200m, savedBill.Payments[0].Amount);
    }

    [Fact]
    public void RecordInvoicePayment_And_RecordBillPayment_NullChecks()
    {
        Assert.Throws<ArgumentNullException>(() => _service.RecordInvoicePayment(null!, new PaymentRecord()));
        Assert.Throws<ArgumentNullException>(() => _service.RecordInvoicePayment(new Invoice(), null!));

        Assert.Throws<ArgumentNullException>(() => _service.RecordBillPayment(null!, new PaymentRecord()));
        Assert.Throws<ArgumentNullException>(() => _service.RecordBillPayment(new Bill(), null!));
        Assert.Throws<ArgumentNullException>(() => _service.VoidBill(null!, "Reason"));
        Assert.Throws<ArgumentNullException>(() => _service.UnvoidBill(null!));
    }

    [Fact]
    public void VoidBill_WithValidUnpaidBill_SetsVoidStatusAndAuditMetadata()
    {
        var bill = new Bill
        {
            Id = "bill-void-1",
            Status = BillStatus.Unpaid,
            Amount = 450m
        };
        _db.Bills.Save(bill);

        _service.VoidBill(bill, "Vendor voided invoice - items returned", "Accountant1", AdminActor);

        var savedBill = _db.Bills.GetById(bill.Id);
        Assert.NotNull(savedBill);
        Assert.Equal(BillStatus.Void, savedBill.Status);
        Assert.Equal("Vendor voided invoice - items returned", savedBill.VoidReason);
        Assert.Equal("Accountant1", savedBill.VoidedBy);
        Assert.NotNull(savedBill.VoidedAt);
        Assert.Equal(0m, savedBill.BalanceDue);
    }

    [Fact]
    public void VoidBill_WithRecordedPayments_ThrowsInvalidOperationException()
    {
        var bill = new Bill
        {
            Id = "bill-void-payments",
            Status = BillStatus.Unpaid,
            Amount = 1000m,
            Payments = [new PaymentRecord { Amount = 500m, Date = DateTime.Today }]
        };
        _db.Bills.Save(bill);

        var ex = Assert.Throws<InvalidOperationException>(() => _service.VoidBill(bill, "Attempting to void paid bill", "User", AdminActor));
        Assert.Contains("payments", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Bill state remains unchanged
        var untouched = _db.Bills.GetById(bill.Id);
        Assert.Equal(BillStatus.Unpaid, untouched.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void VoidBill_WithMissingReason_ThrowsArgumentException(string? reason)
    {
        var bill = new Bill
        {
            Id = "bill-no-reason",
            Status = BillStatus.Unpaid,
            Amount = 200m
        };

        Assert.Throws<ArgumentException>(() => _service.VoidBill(bill, reason!, "User"));
    }

    [Fact]
    public void VoidBill_LinkedToPurchaseOrder_RecalculatesPoTotalBilledAndRevertsStatus()
    {
        const string poId = "po-void-test";
        var po = new PurchaseOrder
        {
            Id = poId,
            PoNumber = "PO-9900",
            ApplyTax = false,
            LineItems = [new PoItem { Quantity = 1, UnitPrice = 1000m }],
            TotalBilled = 1000m,
            Status = PurchaseOrderStatus.FullyBilled
        };
        _db.PurchaseOrders.Save(po);

        var bill1 = new Bill
        {
            Id = "bill-po-1",
            PurchaseOrderId = poId,
            Amount = 600m,
            Status = BillStatus.Paid,
            DatePaid = DateTime.Today
        };
        var bill2 = new Bill
        {
            Id = "bill-po-2",
            PurchaseOrderId = poId,
            Amount = 400m,
            Status = BillStatus.Unpaid
        };
        _db.Bills.Save(bill1);
        _db.Bills.Save(bill2);

        // Void the unpaid bill
        _service.VoidBill(bill2, "Returned materials to supplier", "Buyer", AdminActor);

        var updatedPo = _db.PurchaseOrders.GetById(poId);
        Assert.NotNull(updatedPo);
        Assert.Equal(600m, updatedPo.TotalBilled); // 1000 - 400 = 600
        Assert.Equal(PurchaseOrderStatus.PartiallyBilled, updatedPo.Status);

        var updatedBill = _db.Bills.GetById(bill2.Id);
        Assert.Equal(BillStatus.Void, updatedBill.Status);
    }

    [Fact]
    public void VoidBill_WhenAllBillsVoidedOnPo_RevertsStatusToFinal()
    {
        const string poId = "po-all-void";
        var po = new PurchaseOrder
        {
            Id = poId,
            PoNumber = "PO-9901",
            ApplyTax = false,
            LineItems = [new PoItem { Quantity = 1, UnitPrice = 500m }],
            TotalBilled = 500m,
            Status = PurchaseOrderStatus.FullyBilled
        };
        _db.PurchaseOrders.Save(po);

        var bill = new Bill
        {
            Id = "bill-po-sole",
            PurchaseOrderId = poId,
            Amount = 500m,
            Status = BillStatus.Unpaid
        };
        _db.Bills.Save(bill);

        _service.VoidBill(bill, "Entire shipment returned and cancelled", "Buyer", AdminActor);

        var updatedPo = _db.PurchaseOrders.GetById(poId);
        Assert.NotNull(updatedPo);
        Assert.Equal(0m, updatedPo.TotalBilled);
        Assert.Equal(PurchaseOrderStatus.Final, updatedPo.Status);
    }

    [Fact]
    public void UnvoidBill_RestoresBillToUnpaidAndUpdatesPO()
    {
        const string poId = "po-unvoid-test";
        var po = new PurchaseOrder
        {
            Id = poId,
            PoNumber = "PO-9902",
            ApplyTax = false,
            LineItems = [new PoItem { Quantity = 1, UnitPrice = 800m }],
            TotalBilled = 0m,
            Status = PurchaseOrderStatus.Final
        };
        _db.PurchaseOrders.Save(po);

        var bill = new Bill
        {
            Id = "bill-unvoid-1",
            PurchaseOrderId = poId,
            Amount = 800m,
            Status = BillStatus.Void,
            VoidReason = "Accidental void",
            VoidedBy = "User1",
            VoidedAt = DateTime.UtcNow
        };
        _db.Bills.Save(bill);

        _service.UnvoidBill(bill, "AdminUser", AdminActor);

        var restoredBill = _db.Bills.GetById(bill.Id);
        Assert.NotNull(restoredBill);
        Assert.Equal(BillStatus.Unpaid, restoredBill.Status);
        Assert.Null(restoredBill.VoidReason);
        Assert.Null(restoredBill.VoidedAt);
        Assert.Null(restoredBill.VoidedBy);
        Assert.Equal(800m, restoredBill.BalanceDue);

        var updatedPo = _db.PurchaseOrders.GetById(poId);
        Assert.NotNull(updatedPo);
        Assert.Equal(800m, updatedPo.TotalBilled);
        Assert.Equal(PurchaseOrderStatus.FullyBilled, updatedPo.Status);
    }

    [Fact]
    public void ApproveBill_WithNullBill_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _service.ApproveBill(null!));
    }

    [Fact]
    public void ApproveBill_WithVoidedBill_ThrowsInvalidOperationException()
    {
        var bill = new Bill
        {
            Id = "bill-app-void",
            Status = BillStatus.Void,
            Amount = 100m
        };
        _db.Bills.Save(bill);

        Assert.Throws<InvalidOperationException>(() => _service.ApproveBill(bill, actor: AdminActor));
    }

    [Fact]
    public void ApproveBill_WithUnpaidDraft_SetsStatusUnpaidAndSaves()
    {
        var bill = new Bill
        {
            Id = "bill-app-unpaid",
            Status = BillStatus.AwaitingApproval,
            Amount = 500m
        };
        _db.Bills.Save(bill);

        _service.ApproveBill(bill, "Approver1", AdminActor);

        var approved = _db.Bills.GetById(bill.Id);
        Assert.NotNull(approved);
        Assert.Equal(BillStatus.Unpaid, approved.Status);
        Assert.Equal(500m, approved.BalanceDue);
    }

    [Fact]
    public void ApproveBill_WithExistingPaymentsCoveringTotal_SetsStatusPaid()
    {
        var paymentDate = new DateTime(2026, 10, 1);
        var bill = new Bill
        {
            Id = "bill-app-paid",
            Status = BillStatus.AwaitingApproval,
            Amount = 300m,
            Payments = [new PaymentRecord { Amount = 300m, Date = paymentDate }]
        };
        _db.Bills.Save(bill);

        _service.ApproveBill(bill, "Approver1", AdminActor);

        var approved = _db.Bills.GetById(bill.Id);
        Assert.NotNull(approved);
        Assert.Equal(BillStatus.Paid, approved.Status);
        Assert.Equal(paymentDate, approved.DatePaid);
        Assert.Equal(0m, approved.BalanceDue);
    }

    [Fact]
    public void ApproveBill_LinkedToPurchaseOrder_UpdatesPoTotalBilled()
    {
        const string poId = "po-app-test";
        var po = new PurchaseOrder
        {
            Id = poId,
            PoNumber = "PO-9903",
            ApplyTax = false,
            LineItems = [new PoItem { Quantity = 1, UnitPrice = 1200m }],
            TotalBilled = 0m,
            Status = PurchaseOrderStatus.Final
        };
        _db.PurchaseOrders.Save(po);

        var bill = new Bill
        {
            Id = "bill-app-po",
            PurchaseOrderId = poId,
            Amount = 1200m,
            Status = BillStatus.AwaitingApproval
        };
        _db.Bills.Save(bill);

        _service.ApproveBill(bill, "Buyer", AdminActor);

        var updatedPo = _db.PurchaseOrders.GetById(poId);
        Assert.NotNull(updatedPo);
        Assert.Equal(1200m, updatedPo.TotalBilled);
        Assert.Equal(PurchaseOrderStatus.FullyBilled, updatedPo.Status);
    }

    [Fact]
    public void ApproveBill_WithLinkedEmailInCommunicationContext_MarksEmailApproved()
    {
        var serviceWithComm = new AccountingService(
            _db.AccountingContext,
            _db.ProjectsContext,
            _db.CoreContext,
            communication: _db.CommunicationContext);

        var bill = new Bill
        {
            Id = "bill-linked-email-1",
            Status = BillStatus.AwaitingApproval,
            Amount = 250m
        };
        _db.Bills.Save(bill);

        var email = new EmailMessage
        {
            Id = "email-bill-1",
            Subject = "Invoice 123",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted,
            LinkedBillId = bill.Id
        };
        _db.EmailMessages.Save(email);

        serviceWithComm.ApproveBill(bill, "ManagerBob", AdminActor);

        var updatedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, updatedEmail.BillingStatus);
        Assert.Equal("ManagerBob", updatedEmail.ProcessedByUserId);
        Assert.NotNull(updatedEmail.ProcessedAt);
    }

    #endregion

    [Fact]
    public void UpdateInvoicePayment_EditsAmount_UpdatesStatusAndPersistence()
    {
        const string projectId = "proj-upd-pay";
        _db.Projects.Save(new Project { Id = projectId, Name = "Payment Update Project" });

        var invoice = new Invoice
        {
            Id = "inv-upd-pay-1",
            ProjectId = projectId,
            InvoiceNumber = "INV-UPD-001",
            Status = InvoiceStatus.Final,
            LineItems = [new() { Description = "Services", Quantity = 1, UnitPrice = 1000m }]
        };
        _db.Invoices.Save(invoice);

        var payment = new PaymentRecord
        {
            Id = "pay-1",
            Amount = 500m,
            Date = DateTime.Today.AddDays(-2),
            Method = "Check",
            Reference = "CHK-101",
            Notes = "First half"
        };
        _service.RecordInvoicePayment(invoice, payment, "Admin", AdminActor);

        var intermediate = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(intermediate);
        Assert.Equal(InvoiceStatus.Partial, intermediate.Status);
        Assert.Null(intermediate.DatePaid);

        // Now edit payment to full amount
        var updatedPayment = new PaymentRecord
        {
            Id = "pay-1",
            Amount = invoice.GrandTotal,
            Date = DateTime.Today,
            Method = "Wire",
            Reference = "WIRE-999",
            Notes = "Updated to full"
        };
        _service.UpdateInvoicePayment(intermediate, updatedPayment, "Admin", actor: AdminActor);

        var finalInv = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(finalInv);
        Assert.Equal(InvoiceStatus.Paid, finalInv.Status);
        Assert.NotNull(finalInv.DatePaid);
        Assert.Equal(DateTime.Today, finalInv.DatePaid);
        Assert.Equal("Wire", finalInv.Payments[0].Method);
        Assert.Equal("WIRE-999", finalInv.Payments[0].Reference);
    }

    [Fact]
    public void UpdateInvoicePayment_ReducesBelowPaid_RevertsToPartial()
    {
        const string projectId = "proj-upd-revert";
        _db.Projects.Save(new Project { Id = projectId, Name = "Revert Payment Project" });

        var invoice = new Invoice
        {
            Id = "inv-upd-revert-1",
            ProjectId = projectId,
            InvoiceNumber = "INV-UPD-002",
            Status = InvoiceStatus.Final,
            LineItems = [new() { Description = "Consulting", Quantity = 1, UnitPrice = 1000m }]
        };
        _db.Invoices.Save(invoice);

        var fullAmount = invoice.GrandTotal;
        var payment = new PaymentRecord
        {
            Id = "pay-revert-1",
            Amount = fullAmount,
            Date = DateTime.Today,
            Method = "ACH"
        };
        _service.RecordInvoicePayment(invoice, payment, "BillingUser", AdminActor);

        var paidInv = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(paidInv);
        Assert.Equal(InvoiceStatus.Paid, paidInv.Status);
        Assert.NotNull(paidInv.DatePaid);

        // Reduce payment to partial amount
        var reducedPayment = new PaymentRecord
        {
            Id = "pay-revert-1",
            Amount = fullAmount / 2,
            Date = DateTime.Today,
            Method = "ACH"
        };
        _service.UpdateInvoicePayment(paidInv, reducedPayment, "BillingUser", actor: AdminActor);

        var revertedInv = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(revertedInv);
        Assert.Equal(InvoiceStatus.Partial, revertedInv.Status);
        Assert.Null(revertedInv.DatePaid);
    }

    [Fact]
    public void DeleteInvoicePayment_RemovesPayment_RevertsStatusToFinalWhenEmpty()
    {
        const string projectId = "proj-del-pay";
        _db.Projects.Save(new Project { Id = projectId, Name = "Delete Payment Project" });

        var invoice = new Invoice
        {
            Id = "inv-del-pay-1",
            ProjectId = projectId,
            InvoiceNumber = "INV-DEL-001",
            Status = InvoiceStatus.Final,
            LineItems = [new() { Description = "Design", Quantity = 1, UnitPrice = 500m }]
        };
        _db.Invoices.Save(invoice);

        var payment = new PaymentRecord
        {
            Id = "pay-del-1",
            Amount = invoice.GrandTotal,
            Date = DateTime.Today
        };
        _service.RecordInvoicePayment(invoice, payment, "Admin", AdminActor);

        var paidInv = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(paidInv);
        Assert.Equal(InvoiceStatus.Paid, paidInv.Status);

        // Delete the payment
        _service.DeleteInvoicePayment(paidInv, "pay-del-1", "Admin", actor: AdminActor);

        var emptyInv = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(emptyInv);
        Assert.Empty(emptyInv.Payments);
        Assert.Equal(InvoiceStatus.Final, emptyInv.Status);
        Assert.Null(emptyInv.DatePaid);
        Assert.Equal(emptyInv.GrandTotal, emptyInv.BalanceDue);
    }

    [Fact]
    public void DeleteInvoicePayment_BlockedWhenPrepaymentCreditsConsumed()
    {
        const string projectId = "proj-prepay-guard";
        _db.Projects.Save(new Project { Id = projectId, Name = "Prepayment Guard Project" });

        // Prepayment invoice
        var prepayInvoice = new Invoice
        {
            Id = "inv-prepay-source",
            ProjectId = projectId,
            InvoiceNumber = "INV-PREPAY-001",
            Status = InvoiceStatus.Paid,
            LineItems =
            [
                new()
                {
                    Id = "line-prepay-1",
                    Description = "Deposit",
                    Quantity = 1,
                    UnitPrice = 1000m,
                    IsPrepayment = true
                }
            ],
            Payments =
            [
                new()
                {
                    Id = "pay-prepay-1",
                    Amount = 1000m * (1 + FinancialCalculationEngine.QuebecCombinedTaxRate),
                    Date = DateTime.Today
                }
            ]
        };
        _db.Invoices.Save(prepayInvoice);

        // Consuming invoice
        var consumingInvoice = new Invoice
        {
            Id = "inv-prepay-consumer",
            ProjectId = projectId,
            InvoiceNumber = "INV-CONSUMER-001",
            Status = InvoiceStatus.Final,
            LineItems =
            [
                new()
                {
                    Id = "line-credit-used-1",
                    Description = "Applied Deposit",
                    Quantity = 1,
                    UnitPrice = -1000m,
                    IsCreditUsed = true,
                    SourceId = "line-prepay-1"
                }
            ]
        };
        _db.Invoices.Save(consumingInvoice);

        // Attempting to delete the payment from the prepayment invoice must throw
        Assert.Throws<InvalidOperationException>(() =>
            _service.DeleteInvoicePayment(prepayInvoice, "pay-prepay-1", "Admin", actor: AdminActor));
    }

    [Fact]
    public void UpdateInvoicePayment_BlockedWhenPrepaymentCreditsConsumedAndReduced()
    {
        const string projectId = "proj-prepay-upd-guard";
        _db.Projects.Save(new Project { Id = projectId, Name = "Prepayment Update Guard Project" });

        var prepayInvoice = new Invoice
        {
            Id = "inv-prepay-upd-source",
            ProjectId = projectId,
            InvoiceNumber = "INV-PREPAY-002",
            Status = InvoiceStatus.Paid,
            LineItems =
            [
                new()
                {
                    Id = "line-prepay-2",
                    Description = "Deposit",
                    Quantity = 1,
                    UnitPrice = 1000m,
                    IsPrepayment = true
                }
            ],
            Payments =
            [
                new()
                {
                    Id = "pay-prepay-2",
                    Amount = 1000m * (1 + FinancialCalculationEngine.QuebecCombinedTaxRate),
                    Date = DateTime.Today
                }
            ]
        };
        _db.Invoices.Save(prepayInvoice);

        var consumingInvoice = new Invoice
        {
            Id = "inv-prepay-upd-consumer",
            ProjectId = projectId,
            InvoiceNumber = "INV-CONSUMER-002",
            Status = InvoiceStatus.Final,
            LineItems =
            [
                new()
                {
                    Id = "line-credit-used-2",
                    Description = "Applied Deposit",
                    Quantity = 1,
                    UnitPrice = -1000m,
                    IsCreditUsed = true,
                    SourceId = "line-prepay-2"
                }
            ]
        };
        _db.Invoices.Save(consumingInvoice);

        var reducedPayment = new PaymentRecord
        {
            Id = "pay-prepay-2",
            Amount = 100m,
            Date = DateTime.Today
        };

        Assert.Throws<InvalidOperationException>(() =>
            _service.UpdateInvoicePayment(prepayInvoice, reducedPayment, "Admin", actor: AdminActor));
    }

    [Fact]
    public void UpdateInvoicePayment_And_DeleteInvoicePayment_ValidationChecks()
    {
        Assert.Throws<ArgumentNullException>(() => _service.UpdateInvoicePayment(null!, new PaymentRecord()));
        Assert.Throws<ArgumentNullException>(() => _service.UpdateInvoicePayment(new Invoice(), null!));
        Assert.Throws<ArgumentNullException>(() => _service.DeleteInvoicePayment(null!, "pay-1"));
        Assert.Throws<ArgumentException>(() => _service.DeleteInvoicePayment(new Invoice(), ""));
        Assert.Throws<ArgumentException>(() => _service.DeleteInvoicePayment(new Invoice(), "   "));

        var emptyInvoice = new Invoice { InvoiceNumber = "INV-EMPTY" };
        Assert.Throws<InvalidOperationException>(() => _service.UpdateInvoicePayment(emptyInvoice, new PaymentRecord { Id = "nonexistent" }, actor: AdminActor));
        Assert.Throws<InvalidOperationException>(() => _service.DeleteInvoicePayment(emptyInvoice, "nonexistent", actor: AdminActor));
    }

    [Fact]
    public void DeleteBill_WithRecordedPaymentsAndSourceEmail_CleansBillAndBillPaymentLedgerEntries()
    {
        var serviceWithComm = new AccountingService(
            _db.AccountingContext,
            _db.ProjectsContext,
            _db.CoreContext,
            communication: _db.CommunicationContext,
            ledgerService: new LedgerService(_db.AccountingContext, _db.ProjectsContext));

        var email = new EmailMessage
        {
            Id = "email-paid-bill-1",
            Subject = "Customer Payment Filed as Bill",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved
        };

        var bill = new Bill
        {
            Id = "bill-paid-delete-1",
            SupplierName = "Client Who Paid Us",
            InvoiceNumber = "INV-99",
            Amount = 500m,
            SubTotal = 500m,
            Status = BillStatus.Paid,
            Date = DateTime.Today,
            DatePaid = DateTime.Today,
            SourceEmailId = email.Id,
            Payments = [new PaymentRecord { Id = "pmt-1", Amount = 500m, Date = DateTime.Today, Method = "Bank" }]
        };
        serviceWithComm.SaveBill(bill, "Tester", AdminActor);

        email.LinkedBillId = bill.Id;
        email.LinkedBillIds = [bill.Id];
        _db.EmailMessages.Save(email);

        Assert.NotNull(_db.JournalEntries!.GetBySource(JournalSourceType.Bill, bill.Id));
        Assert.NotNull(_db.JournalEntries.GetBySource(JournalSourceType.BillPayment, $"{bill.Id}:pmt-1"));

        serviceWithComm.DeleteBill(bill.Id, AdminActor);

        Assert.Null(_db.Bills.GetById(bill.Id));
        Assert.Null(_db.JournalEntries.GetBySource(JournalSourceType.Bill, bill.Id));
        Assert.Null(_db.JournalEntries.GetBySource(JournalSourceType.BillPayment, $"{bill.Id}:pmt-1"));

        var reloadedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(reloadedEmail);
        Assert.Null(reloadedEmail!.LinkedBillId);
        Assert.Empty(reloadedEmail.LinkedBillIds);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Unprocessed, reloadedEmail.BillingStatus);
    }

    [Fact]
    public void MergeRecurringExpenses_MovesHistoryBillsAndEmailLinksToTargetAndDeletesSources()
    {
        var serviceWithComm = new AccountingService(
            _db.AccountingContext,
            _db.ProjectsContext,
            _db.CoreContext,
            communication: _db.CommunicationContext,
            ledgerService: new LedgerService(_db.AccountingContext, _db.ProjectsContext));

        var target = new RecurringExpense
        {
            Id = "rec-target",
            Name = "Internet Fiber",
            Supplier = "Bell Canada",
            Frequency = RecurringExpenseFrequency.Monthly,
            Amount = 120m,
            History = [new RecurringBillRecord { Id = "hist-1", InvoiceNumber = "B-01", Date = new DateTime(2026, 1, 1), Amount = 120m }]
        };
        var dup1 = new RecurringExpense
        {
            Id = "rec-dup-1",
            Name = "Bell Internet (Auto)",
            Supplier = "Bell Canada",
            GlAccountCode = "5620",
            Frequency = RecurringExpenseFrequency.Monthly,
            Amount = 120m,
            History = [new RecurringBillRecord { Id = "hist-2", InvoiceNumber = "B-02", Date = new DateTime(2026, 2, 1), Amount = 125m }]
        };
        _db.RecurringExpenses.Save(target);
        _db.RecurringExpenses.Save(dup1);

        var linkedBill = new Bill
        {
            Id = "bill-dup-rec",
            SupplierName = "Bell Canada",
            LinkedRecurringExpenseId = dup1.Id,
            Amount = 125m,
            SubTotal = 125m
        };
        _db.Bills.Save(linkedBill);

        var linkedEmail = new EmailMessage
        {
            Id = "email-dup-rec",
            Subject = "Bell Statement",
            LinkedRecurringExpenseId = dup1.Id
        };
        _db.EmailMessages.Save(linkedEmail);

        var mergedCount = serviceWithComm.MergeRecurringExpenses(target.Id, [dup1.Id], AdminActor);

        Assert.Equal(1, mergedCount);
        Assert.Null(_db.RecurringExpenses.GetById(dup1.Id));

        var reloadedTarget = _db.RecurringExpenses.GetById(target.Id);
        Assert.NotNull(reloadedTarget);
        Assert.Equal(2, reloadedTarget!.History.Count);
        Assert.Equal("5620", reloadedTarget.GlAccountCode);

        Assert.Equal(target.Id, _db.Bills.GetById(linkedBill.Id)!.LinkedRecurringExpenseId);
        Assert.Equal(target.Id, _db.EmailMessages.GetById(linkedEmail.Id)!.LinkedRecurringExpenseId);
    }

    [Fact]
    public void CalculateProjectGrossMargin_ExcludesDraftExpenseReports()
    {
        const string projectId = "proj-draft-margin";
        _db.Projects.Save(new Project { Id = projectId, Name = "Draft Expense Filter" });
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-draft-margin",
            ProjectId = projectId,
            Status = InvoiceStatus.Sent,
            LineItems = [new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 1000m }]
        });
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-submitted-margin",
            DateSubmitted = DateTime.Today,
            LineItems = [new ExpenseItem { ProjectId = projectId, Amount = 250m }]
        });
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-draft-margin",
            DateSubmitted = null,
            LineItems = [new ExpenseItem { ProjectId = projectId, Amount = 500m }]
        });

        var margin = _service.CalculateProjectGrossMargin(projectId);

        Assert.Equal(750m, margin);
    }

    [Fact]
    public void CalculateProjectGrossMargin_ExcludesDraftInvoices_AndFallsBackToAcceptedQuote()
    {
        const string projectId = "proj-draft-inv-margin";
        _db.Projects.Save(new Project { Id = projectId, Name = "Draft Invoice Margin Isolation" });
        _db.Quotes.Save(new Quote
        {
            Id = "q-accepted-draft-inv",
            ProjectId = projectId,
            Status = QuoteStatus.Accepted,
            MaterialLineItems = [new QuoteItemRow { Quantity = 1m, UnitPrice = 2000m }]
        });
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-draft-only",
            ProjectId = projectId,
            Status = InvoiceStatus.Draft,
            LineItems = [new InvoiceLine { Description = "Unfinalized work", Quantity = 1m, UnitPrice = 9500m }]
        });

        var margin = _service.CalculateProjectGrossMargin(projectId);

        // Draft invoice must be ignored, falling back cleanly to Accepted quote revenue ($2000)
        Assert.Equal(2000m, margin);
    }

    [Fact]
    public void EvaluateProjectCommissions_ExcludesDraftInvoices_DoesNotBlockOrInflateBase()
    {
        const string projectId = "proj-draft-inv-comm";
        _db.Projects.Save(new Project
        {
            Id = projectId,
            Name = "Draft Invoice Commissions",
            Status = ProjectStatus.Completed,
            Commissions =
            [
                new CommissionBeneficiary
                {
                    Name = "Sam Sales",
                    Percentage = 10m,
                    CommissionBasis = CommissionBasisTypes.Revenue
                }
            ]
        });

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-paid-comm",
            ProjectId = projectId,
            Status = InvoiceStatus.Paid,
            LineItems = [new InvoiceLine { Description = "Delivered", Quantity = 1m, UnitPrice = 5000m }]
        });

        // Unfinalized Draft invoice must neither block allPaid nor inflate revenue base
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-draft-comm",
            ProjectId = projectId,
            Status = InvoiceStatus.Draft,
            LineItems = [new InvoiceLine { Description = "Draft", Quantity = 1m, UnitPrice = 15000m }]
        });

        _service.EvaluateProjectCommissions(projectId);

        var records = _db.CommissionLedgerRecords.GetByProject(projectId);
        var record = Assert.Single(records);
        Assert.Equal(5000m, record.BaseAmount);
        Assert.Equal(500m, record.CommissionAmount);
    }

    [Fact]
    public void HasConsumedPrepaymentCredits_IgnoresDraftInvoices()
    {
        const string projectId = "proj-draft-credit";
        var prepayInvoice = new Invoice
        {
            Id = "inv-prepay-src",
            ProjectId = projectId,
            Status = InvoiceStatus.Paid,
            LineItems = [new InvoiceLine { Id = "prepay-line-1", Quantity = 1m, UnitPrice = 1000m, IsPrepayment = true }]
        };
        _db.Invoices.Save(prepayInvoice);

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-draft-consumer",
            ProjectId = projectId,
            Status = InvoiceStatus.Draft,
            LineItems = [new InvoiceLine { Quantity = 1m, UnitPrice = -500m, IsCreditUsed = true, SourceId = "prepay-line-1" }]
        });

        Assert.False(_service.HasConsumedPrepaymentCredits(prepayInvoice));
    }

    [Fact]
    public void ApplySuggestedInvoicePayment_RejectsDraftInvoice()
    {
        const string projectId = "proj-draft-sug";
        _db.Projects.Save(new Project { Id = projectId, Name = "Draft Suggestion" });
        var draft = new Invoice
        {
            Id = "inv-draft-sug",
            ProjectId = projectId,
            InvoiceNumber = "INV-DRAFT-1",
            Status = InvoiceStatus.Draft,
            LineItems = [new InvoiceLine { Quantity = 1m, UnitPrice = 500m }]
        };
        _db.Invoices.Save(draft);

        var sug = new SuggestedInvoicePayment
        {
            Id = "sug-draft-1",
            InvoiceId = draft.Id,
            InvoiceNumber = draft.InvoiceNumber,
            InvoiceStatus = InvoiceStatus.Draft,
            SuggestedAmount = draft.GrandTotal,
            Status = SuggestedInvoicePaymentStatus.Pending
        };
        _db.SuggestedInvoicePayments.Save(sug);

        Assert.Throws<InvalidOperationException>(() =>
            _service.ApplySuggestedInvoicePayment(sug.Id, user: AdminActor.FullName, actor: AdminActor));

        // Draft remains untouched and unfinalized
        Assert.Equal(InvoiceStatus.Draft, _db.Invoices.GetById(draft.Id)!.Status);
        Assert.Empty(_db.Invoices.GetById(draft.Id)!.Payments);
    }

    [Fact]
    public void AccountingHealthService_EvaluateHealth_CompletelyIgnoresDraftInvoices()
    {
        const string projectId = "proj-health-draft";
        _db.Projects.Save(new Project { Id = projectId, Name = "Health Draft Project" });

        // Stale 60-day-old Draft invoice must NEVER trigger health warnings or block subledger-cleanup
        var staleDraft = new Invoice
        {
            Id = "inv-stale-draft",
            ProjectId = projectId,
            InvoiceNumber = "INV-STALE-DRAFT",
            Date = DateTime.Today.AddDays(-60),
            DueDate = DateTime.Today.AddDays(-30),
            Status = InvoiceStatus.Draft,
            LineItems = [new InvoiceLine { Description = "Draft", Quantity = 1m, UnitPrice = 2500m }]
        };
        _db.Invoices.Save(staleDraft);

        // Legacy pending suggestion targeting a Draft invoice must also be ignored
        _db.SuggestedInvoicePayments.Save(new SuggestedInvoicePayment
        {
            Id = "sug-legacy-draft",
            InvoiceId = staleDraft.Id,
            InvoiceNumber = staleDraft.InvoiceNumber,
            InvoiceStatus = InvoiceStatus.Draft,
            SuggestedAmount = staleDraft.GrandTotal,
            Status = SuggestedInvoicePaymentStatus.Pending
        });

        var ledger = new LedgerService(_db.AccountingContext, _db.ProjectsContext);
        var healthService = new AccountingHealthService(_db.AccountingContext, ledger, projects: _db.ProjectsContext);
        var report = healthService.EvaluateHealth(DateTime.Today, AdminActor);

        Assert.DoesNotContain(report.Issues, i => i.Code == "AR_STALE_DRAFTS");
        Assert.DoesNotContain(report.Issues, i => i.Code == "AR_SUGGESTED_PAYMENTS");
        Assert.DoesNotContain(report.Issues, i => i.Code == "GL_MISSING_JES");
        var cleanupStep = report.CloseChecklist.Single(s => s.Key == "subledger-cleanup");
        Assert.True(cleanupStep.IsComplete);
    }
}