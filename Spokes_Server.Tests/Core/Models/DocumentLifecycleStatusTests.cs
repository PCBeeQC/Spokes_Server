using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models;

public class DocumentLifecycleStatusTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly AccountingService _accountingService;
    private static readonly Employee AdminActor = new() { Id = "doc-admin", FirstName = "Doc", LastName = "Admin", IsActive = true, IsAdmin = true };

    public DocumentLifecycleStatusTests()
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
        _accountingService = new AccountingService(_db.AccountingContext, _db.ProjectsContext, _db.CoreContext);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void QuoteStatus_GetAllowedTransitions_DraftMustBecomeFinalBeforeAcceptedOrRejected()
    {
        var draftAllowed = QuoteStatus.GetAllowedTransitions(QuoteStatus.Draft);
        Assert.Equal([QuoteStatus.Draft, QuoteStatus.Final], draftAllowed);
        Assert.DoesNotContain(QuoteStatus.Accepted, draftAllowed);
        Assert.DoesNotContain(QuoteStatus.Rejected, draftAllowed);

        var finalAllowed = QuoteStatus.GetAllowedTransitions(QuoteStatus.Final);
        Assert.Equal([QuoteStatus.Draft, QuoteStatus.Final, QuoteStatus.Accepted, QuoteStatus.Rejected], finalAllowed);

        var acceptedAllowed = QuoteStatus.GetAllowedTransitions(QuoteStatus.Accepted);
        Assert.Equal([QuoteStatus.Draft, QuoteStatus.Final, QuoteStatus.Accepted, QuoteStatus.Rejected], acceptedAllowed);
    }

    [Theory]
    [InlineData(QuoteStatus.Draft, QuoteStatus.Final, false)]
    [InlineData(QuoteStatus.Final, QuoteStatus.Accepted, false)]
    [InlineData(QuoteStatus.Final, QuoteStatus.Rejected, false)]
    [InlineData(QuoteStatus.Final, QuoteStatus.Draft, true)]
    [InlineData(QuoteStatus.Accepted, QuoteStatus.Final, true)]
    [InlineData(QuoteStatus.Accepted, QuoteStatus.Draft, true)]
    [InlineData(QuoteStatus.Rejected, QuoteStatus.Final, true)]
    [InlineData(QuoteStatus.Rejected, QuoteStatus.Draft, true)]
    [InlineData(QuoteStatus.Accepted, QuoteStatus.Rejected, true)]
    [InlineData(QuoteStatus.Rejected, QuoteStatus.Accepted, true)]
    public void QuoteStatus_RequiresReason_DetectsRegressionsAndLateralSwitches(string oldStatus, string newStatus, bool expected)
    {
        Assert.Equal(expected, QuoteStatus.RequiresReason(oldStatus, newStatus));
    }

    [Theory]
    [InlineData(QuoteStatus.Draft, false)]
    [InlineData(QuoteStatus.Final, true)]
    [InlineData(QuoteStatus.Accepted, false)]
    [InlineData(QuoteStatus.Rejected, false)]
    public void QuoteStatus_CanUnlockEdit_OnlyFinalReturnsTrue(string status, bool expected)
    {
        Assert.Equal(expected, QuoteStatus.CanUnlockEdit(status));
    }

    [Fact]
    public void InvoiceStatus_GetDropdownStatuses_ExcludesPaidAndLocksWhenPaymentsExist()
    {
        var draftOptions = InvoiceStatus.GetDropdownStatuses(InvoiceStatus.Draft, hasPayments: false);
        Assert.Equal([InvoiceStatus.Draft, InvoiceStatus.Final], draftOptions);
        Assert.DoesNotContain(InvoiceStatus.Paid, draftOptions);

        var finalNoPayments = InvoiceStatus.GetDropdownStatuses(InvoiceStatus.Final, hasPayments: false);
        Assert.Equal([InvoiceStatus.Draft, InvoiceStatus.Final], finalNoPayments);

        var partialWithPayments = InvoiceStatus.GetDropdownStatuses(InvoiceStatus.Partial, hasPayments: true);
        Assert.Equal([InvoiceStatus.Partial], partialWithPayments);

        var paidWithPayments = InvoiceStatus.GetDropdownStatuses(InvoiceStatus.Paid, hasPayments: true);
        Assert.Equal([InvoiceStatus.Paid], paidWithPayments);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, InvoiceStatus.Final, false)]
    [InlineData(InvoiceStatus.Final, InvoiceStatus.Draft, true)]
    [InlineData(InvoiceStatus.Paid, InvoiceStatus.Final, true)]
    [InlineData(InvoiceStatus.Partial, InvoiceStatus.Final, true)]
    public void InvoiceStatus_RequiresReason_DetectsBackwardTransitions(string oldStatus, string newStatus, bool expected)
    {
        Assert.Equal(expected, InvoiceStatus.RequiresReason(oldStatus, newStatus));
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, false)]
    [InlineData(InvoiceStatus.Final, true)]
    [InlineData(InvoiceStatus.Partial, false)]
    [InlineData(InvoiceStatus.Paid, false)]
    [InlineData(InvoiceStatus.Void, false)]
    public void InvoiceStatus_CanUnlockEdit_OnlyFinalReturnsTrue(string status, bool expected)
    {
        Assert.Equal(expected, InvoiceStatus.CanUnlockEdit(status));
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, false)]
    [InlineData(InvoiceStatus.Void, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(InvoiceStatus.Final, true)]
    [InlineData(InvoiceStatus.Sent, true)]
    [InlineData(InvoiceStatus.Partial, true)]
    [InlineData(InvoiceStatus.Paid, true)]
    [InlineData(InvoiceStatus.WrittenOff, true)]
    public void InvoiceStatus_IsPosted_ExcludesDraftVoidAndEmpty(string? status, bool expected)
    {
        Assert.Equal(expected, InvoiceStatus.IsPosted(status));
    }

    [Fact]
    public void AccountingService_PaymentLifecycle_RecordsDocumentHistoryWithReasons()
    {
        const string projectId = "proj-hist-1";
        _db.Projects.Save(new Project { Id = projectId, Name = "History Project" });

        var invoice = new Invoice
        {
            Id = "inv-hist-1",
            ProjectId = projectId,
            InvoiceNumber = "INV-HIST-001",
            Status = InvoiceStatus.Final,
            TaxRate = 0m,
            LineItems = [new InvoiceLine { Description = "Engineering", Quantity = 1, UnitPrice = 1000m }]
        };
        _db.Invoices.Save(invoice);

        var payment = new PaymentRecord
        {
            Id = "pay-hist-1",
            Amount = 1000m,
            Date = DateTime.Today,
            Method = "Wire"
        };

        // 1. Record payment -> Paid
        _accountingService.RecordInvoicePayment(invoice, payment, "Alice Senior", AdminActor);
        var saved = _db.Invoices.GetById(invoice.Id)!;
        Assert.Equal(InvoiceStatus.Paid, saved.Status);
        Assert.Single(saved.History);
        Assert.Equal(DocumentHistoryActions.PaymentRecorded, saved.History[0].Action);
        Assert.Equal(InvoiceStatus.Final, saved.History[0].OldStatus);
        Assert.Equal(InvoiceStatus.Paid, saved.History[0].NewStatus);
        Assert.Equal("Alice Senior", saved.History[0].ChangedBy);

        // 2. Reduce payment -> Partial with reason
        var reduced = new PaymentRecord
        {
            Id = "pay-hist-1",
            Amount = 400m,
            Date = DateTime.Today,
            Method = "Wire"
        };
        _accountingService.UpdateInvoicePayment(saved, reduced, "Alice Senior", "Entered wrong wire amount", AdminActor);
        saved = _db.Invoices.GetById(invoice.Id)!;
        Assert.Equal(InvoiceStatus.Partial, saved.Status);
        Assert.Equal(2, saved.History.Count);
        Assert.Equal(DocumentHistoryActions.PaymentUpdated, saved.History[1].Action);
        Assert.Equal(InvoiceStatus.Paid, saved.History[1].OldStatus);
        Assert.Equal(InvoiceStatus.Partial, saved.History[1].NewStatus);
        Assert.Equal("Entered wrong wire amount", saved.History[1].Reason);

        // 3. Delete payment -> Final with reason
        _accountingService.DeleteInvoicePayment(saved, "pay-hist-1", "Alice Senior", "Wire was for another invoice", AdminActor);
        saved = _db.Invoices.GetById(invoice.Id)!;
        Assert.Equal(InvoiceStatus.Final, saved.Status);
        Assert.Equal(3, saved.History.Count);
        Assert.Equal(DocumentHistoryActions.PaymentDeleted, saved.History[2].Action);
        Assert.Equal(InvoiceStatus.Partial, saved.History[2].OldStatus);
        Assert.Equal(InvoiceStatus.Final, saved.History[2].NewStatus);
        Assert.Equal("Wire was for another invoice", saved.History[2].Reason);
    }

    [Fact]
    public void AppPermissions_ContainsApproveQuotesAndApproveInvoices()
    {
        var all = AppPermissions.GetAll();
        Assert.Contains(AppPermissions.Projects.ApproveQuotes, all["Projects"]);
        Assert.Contains(AppPermissions.Finance.ApproveInvoices, all["Finance"]);
        Assert.NotEqual(AppPermissions.Projects.ApproveQuotes, AppPermissions.GetDescription(AppPermissions.Projects.ApproveQuotes));
        Assert.NotEqual(AppPermissions.Finance.ApproveInvoices, AppPermissions.GetDescription(AppPermissions.Finance.ApproveInvoices));
    }
}
