using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Tests;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Projects;

public class TimelineEventServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly TimelineEventService _service;

    public TimelineEventServiceTests()
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
        _service = new TimelineEventService(_db.ProjectsContext);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Constructor_WithNullDatabase_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new TimelineEventService((ProjectsContext)null!));
    }

    #region Quote Status Changes

    [Fact]
    public void ProcessQuoteStatusChange_DraftToFinal_AddsSystemEvent_AndFinalToDraft_RemovesSystemEvent()
    {
        // Arrange
        var projectId = "proj_quote_1";
        var quote = new Quote
        {
            Id = "quote_1",
            ProjectId = projectId,
            QuoteNumber = "Q-1001",
            Status = "Draft"
        };
        _db.Quotes.Save(quote);

        // Act 1: Draft -> Final
        _service.SyncQuoteStatusTimelineEvent(quote, "Draft", "Final", "AdminUser");

        // Assert 1: Note created
        var notes = _db.ProjectNotes.GetByProject(projectId);
        var finalNote = notes.FirstOrDefault(n => n.SystemReferenceId == $"Quote_Final_{quote.Id}");
        Assert.NotNull(finalNote);
        Assert.Equal("Quote #Q-1001 Finalized", finalNote.Title);
        Assert.Equal("Quote #Q-1001 was marked as Final.", finalNote.Content);
        Assert.Equal("AdminUser", finalNote.CreatedBy);
        Assert.True(finalNote.IsSystemEvent);
        Assert.Equal("Status Update", finalNote.Category);

        // Act 2: Final -> Draft
        _service.SyncQuoteStatusTimelineEvent(quote, "Final", "Draft", "AdminUser");

        // Assert 2: Note removed
        notes = _db.ProjectNotes.GetByProject(projectId);
        Assert.DoesNotContain(notes, n => n.SystemReferenceId == $"Quote_Final_{quote.Id}");
    }

    [Fact]
    public void ProcessQuoteStatusChange_AcceptedOrRejected_AddsResolutionEvent()
    {
        // Arrange
        var projectId = "proj_quote_2";
        var quote = new Quote
        {
            Id = "quote_2",
            ProjectId = projectId,
            QuoteNumber = "Q-1002",
            Status = "Final"
        };
        _db.Quotes.Save(quote);

        // Act 1: Final -> Accepted
        _service.SyncQuoteStatusTimelineEvent(quote, "Final", "Accepted", "Alice");

        // Assert 1: Accepted resolution event added
        var notes = _db.ProjectNotes.GetByProject(projectId);
        var acceptedNote = notes.FirstOrDefault(n => n.SystemReferenceId == $"Quote_Resolution_{quote.Id}");
        Assert.NotNull(acceptedNote);
        Assert.Equal("Quote #Q-1002 Accepted", acceptedNote.Title);
        Assert.Equal("Quote #Q-1002 was accepted.", acceptedNote.Content);
        Assert.Equal("Alice", acceptedNote.CreatedBy);
        Assert.True(acceptedNote.IsSystemEvent);

        // Act 2: Accepted -> Rejected (replaces resolution event without duplicates)
        _service.SyncQuoteStatusTimelineEvent(quote, "Accepted", "Rejected", "Bob");

        // Assert 2: Single note updated with Rejected
        notes = _db.ProjectNotes.GetByProject(projectId);
        var resolutionNotes = notes.Where(n => n.SystemReferenceId == $"Quote_Resolution_{quote.Id}").ToList();
        Assert.Single(resolutionNotes);
        Assert.Equal("Quote #Q-1002 Rejected", resolutionNotes[0].Title);
        Assert.Equal("Quote #Q-1002 was rejected.", resolutionNotes[0].Content);
        Assert.Equal("Bob", resolutionNotes[0].CreatedBy);

        // Act 3: Reverting Rejected to Draft removes resolution note
        _service.SyncQuoteStatusTimelineEvent(quote, "Rejected", "Draft", "Alice");

        notes = _db.ProjectNotes.GetByProject(projectId);
        Assert.DoesNotContain(notes, n => n.SystemReferenceId == $"Quote_Resolution_{quote.Id}");
    }

    [Fact]
    public void ProcessQuoteStatusChange_SameStatusOrNullOrNoProject_DoesNothing()
    {
        // Arrange
        var quote = new Quote
        {
            Id = "quote_edge",
            ProjectId = "proj_quote_edge",
            QuoteNumber = "Q-9999",
            Status = "Draft"
        };
        _db.Quotes.Save(quote);

        // Act & Assert
        // Null quote
        _service.SyncQuoteStatusTimelineEvent(null!, "Draft", "Final", "User");
        Assert.Empty(_db.ProjectNotes.GetByProject("proj_quote_edge"));

        // Empty ProjectId
        var unattachedQuote = new Quote { Id = "quote_unattached", ProjectId = "", Status = "Draft" };
        _service.SyncQuoteStatusTimelineEvent(unattachedQuote, "Draft", "Final", "User");
        Assert.Empty(_db.ProjectNotes.GetAll());

        // Same status
        _service.SyncQuoteStatusTimelineEvent(quote, "Draft", "Draft", "User");
        Assert.Empty(_db.ProjectNotes.GetByProject("proj_quote_edge"));
    }

    [Fact]
    public void ProcessQuoteStatusChange_WhenUserIsEmpty_SetsSystemAsCreatedBy()
    {
        // Arrange
        var projectId = "proj_quote_user";
        var quote = new Quote
        {
            Id = "quote_user",
            ProjectId = projectId,
            QuoteNumber = "Q-500",
            Status = "Draft"
        };
        _db.Quotes.Save(quote);

        // Act
        _service.SyncQuoteStatusTimelineEvent(quote, "Draft", "Final", "");

        // Assert
        var note = _db.ProjectNotes.GetByProject(projectId).FirstOrDefault(n => n.SystemReferenceId == $"Quote_Final_{quote.Id}");
        Assert.NotNull(note);
        Assert.Equal("System", note.CreatedBy);
    }

    #endregion

    #region Invoice Status Changes

    [Fact]
    public void ProcessInvoiceStatusChange_DraftToFinal_AndPaid_AddsSystemEvents()
    {
        // Arrange
        var project = new Project { Id = "proj_inv_1", Name = "Invoice Test Proj", Status = "Draft" };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv_1",
            ProjectId = project.Id,
            InvoiceNumber = "INV-1001",
            Status = "Draft"
        };
        _db.Invoices.Save(invoice);

        // Act 1: Draft -> Final
        _service.SyncInvoiceStatusTimelineEvent(invoice, "Draft", "Final", "Manager");

        // Assert 1: Invoice finalized note added
        var notes = _db.ProjectNotes.GetByProject(project.Id);
        var finalNote = notes.FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Final_{invoice.Id}");
        Assert.NotNull(finalNote);
        Assert.Equal("Invoice #INV-1001 Finalized", finalNote.Title);
        Assert.Equal("Invoice #INV-1001 was marked as Final.", finalNote.Content);
        Assert.Equal("Manager", finalNote.CreatedBy);

        // Act 2: Final -> Paid
        _service.SyncInvoiceStatusTimelineEvent(invoice, "Final", "Paid", "Manager");

        // Assert 2: Invoice paid note added
        notes = _db.ProjectNotes.GetByProject(project.Id);
        var paidNote = notes.FirstOrDefault(n => n.SystemReferenceId == $"Invoice_Paid_{invoice.Id}");
        Assert.NotNull(paidNote);
        Assert.Equal("Invoice #INV-1001 Paid", paidNote.Title);
        Assert.Equal("Invoice #INV-1001 has been paid in full.", paidNote.Content);
    }

    [Fact]
    public void ProcessInvoiceStatusChange_FinalToDraft_AndUnpaying_RemovesSystemEvents()
    {
        // Arrange
        var project = new Project { Id = "proj_inv_2", Name = "Invoice Test Proj 2", Status = "Draft" };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv_2",
            ProjectId = project.Id,
            InvoiceNumber = "INV-1002",
            Status = "Paid"
        };
        _db.Invoices.Save(invoice);

        // Seed both notes
        _service.SyncInvoiceStatusTimelineEvent(invoice, "Draft", "Final", "User");
        _service.SyncInvoiceStatusTimelineEvent(invoice, "Final", "Paid", "User");

        var notes = _db.ProjectNotes.GetByProject(project.Id);
        Assert.Contains(notes, n => n.SystemReferenceId == $"Invoice_Final_{invoice.Id}");
        Assert.Contains(notes, n => n.SystemReferenceId == $"Invoice_Paid_{invoice.Id}");

        // Act 1: Unpay (Paid -> Final)
        _service.SyncInvoiceStatusTimelineEvent(invoice, "Paid", "Final", "User");

        // Assert 1: Paid note removed, Final note still present
        notes = _db.ProjectNotes.GetByProject(project.Id);
        Assert.DoesNotContain(notes, n => n.SystemReferenceId == $"Invoice_Paid_{invoice.Id}");
        Assert.Contains(notes, n => n.SystemReferenceId == $"Invoice_Final_{invoice.Id}");

        // Act 2: Final -> Draft
        _service.SyncInvoiceStatusTimelineEvent(invoice, "Final", "Draft", "User");

        // Assert 2: Final note removed
        notes = _db.ProjectNotes.GetByProject(project.Id);
        Assert.DoesNotContain(notes, n => n.SystemReferenceId == $"Invoice_Final_{invoice.Id}");
    }

    [Fact]
    public void ProcessInvoiceStatusChange_NullOrSameStatusOrEmptyProjectId_DoesNothing()
    {
        var invoice = new Invoice { Id = "inv_edge", ProjectId = "proj_edge", InvoiceNumber = "INV-E" };
        _db.Invoices.Save(invoice);

        // Null invoice
        _service.SyncInvoiceStatusTimelineEvent(null!, "Draft", "Final", "User");
        Assert.Empty(_db.ProjectNotes.GetByProject("proj_edge"));

        // Empty ProjectId
        var emptyProjInvoice = new Invoice { Id = "inv_noproj", ProjectId = "", InvoiceNumber = "INV-NP" };
        _service.SyncInvoiceStatusTimelineEvent(emptyProjInvoice, "Draft", "Final", "User");
        Assert.Empty(_db.ProjectNotes.GetAll());

        // Same status
        _service.SyncInvoiceStatusTimelineEvent(invoice, "Draft", "Draft", "User");
        Assert.Empty(_db.ProjectNotes.GetByProject("proj_edge"));
    }

    #endregion

    #region Document Status Changes

    [Fact]
    public void ProcessDocumentStatusChange_DraftToFinal_AddsEvent_AndFinalToDraft_RemovesEvent()
    {
        // Arrange
        var projectId = "proj_doc_1";
        var doc = new ProjectDocument
        {
            Id = "doc_1",
            ProjectId = projectId,
            Name = "Safety Plan",
            Status = "Draft"
        };
        _db.ProjectDocuments.Save(doc);

        // Act 1: Draft -> Final
        _service.SyncDocumentStatusTimelineEvent(doc, "Draft", "Final", "SafetyOfficer");

        // Assert 1: Note created
        var notes = _db.ProjectNotes.GetByProject(projectId);
        var note = notes.FirstOrDefault(n => n.SystemReferenceId == $"Doc_Final_{doc.Id}");
        Assert.NotNull(note);
        Assert.Equal("'Safety Plan' Finalized", note.Title);
        Assert.Equal("Document 'Safety Plan' was finalized.", note.Content);
        Assert.Equal("SafetyOfficer", note.CreatedBy);
        Assert.True(note.IsSystemEvent);

        // Act 2: Final -> Draft
        _service.SyncDocumentStatusTimelineEvent(doc, "Final", "Draft", "SafetyOfficer");

        // Assert 2: Note removed
        notes = _db.ProjectNotes.GetByProject(projectId);
        Assert.DoesNotContain(notes, n => n.SystemReferenceId == $"Doc_Final_{doc.Id}");
    }

    [Fact]
    public void ProcessDocumentStatusChange_NullOrSameStatusOrEmptyProjectId_DoesNothing()
    {
        var doc = new ProjectDocument { Id = "doc_edge", ProjectId = "proj_doc_edge", Name = "Doc Edge" };
        _db.ProjectDocuments.Save(doc);

        // Null doc
        _service.SyncDocumentStatusTimelineEvent(null!, "Draft", "Final", "User");
        Assert.Empty(_db.ProjectNotes.GetByProject("proj_doc_edge"));

        // Empty ProjectId
        var emptyDoc = new ProjectDocument { Id = "doc_noproj", ProjectId = "", Name = "No Proj" };
        _service.SyncDocumentStatusTimelineEvent(emptyDoc, "Draft", "Final", "User");
        Assert.Empty(_db.ProjectNotes.GetAll());

        // Same status
        _service.SyncDocumentStatusTimelineEvent(doc, "Draft", "Draft", "User");
        Assert.Empty(_db.ProjectNotes.GetByProject("proj_doc_edge"));
    }

    #endregion
}
