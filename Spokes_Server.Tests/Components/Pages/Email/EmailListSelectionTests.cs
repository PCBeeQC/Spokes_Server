using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Components.Pages.Email;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Email;

public class EmailListSelectionTests : BunitContext
{
    public EmailListSelectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    [Fact]
    public void EmailList_WhenFirstEmailDeleted_CorrectlySelectsNextEmail()
    {
        // Arrange
        var msg1 = new EmailMessage { Id = "msg-1", Subject = "Email One", FromAddress = "alice@example.com", Date = DateTimeOffset.UtcNow };
        var msg2 = new EmailMessage { Id = "msg-2", Subject = "Email Two", FromAddress = "bob@example.com", Date = DateTimeOffset.UtcNow };
        var msg3 = new EmailMessage { Id = "msg-3", Subject = "Email Three", FromAddress = "carol@example.com", Date = DateTimeOffset.UtcNow };

        var initialList = new List<EmailMessage> { msg1, msg2, msg3 };

        // Act - initial render with msg1 selected
        var cut = Render<EmailList>(parameters => parameters
            .Add(p => p.Messages, initialList)
            .Add(p => p.SelectedMessageId, "msg-1"));

        var itemsBefore = cut.FindAll(".mud-list-item");
        Assert.Equal(3, itemsBefore.Count);
        Assert.True(itemsBefore[0].ClassList.Contains("mud-selected-item"), "First email should initially be selected");
        Assert.False(itemsBefore[1].ClassList.Contains("mud-selected-item"), "Second email should initially not be selected");
        Assert.False(itemsBefore[2].ClassList.Contains("mud-selected-item"), "Third email should initially not be selected");

        // Act - simulate deleting msg-1: list now has [msg2, msg3], selected is msg2
        var updatedList = new List<EmailMessage> { msg2, msg3 };
        cut.Render(parameters => parameters
            .Add(p => p.Messages, updatedList)
            .Add(p => p.SelectedMessageId, "msg-2"));

        // Assert
        var itemsAfter = cut.FindAll(".mud-list-item");
        Assert.Equal(2, itemsAfter.Count);
        Assert.Contains("Email Two", itemsAfter[0].TextContent);
        Assert.Contains("Email Three", itemsAfter[1].TextContent);

        // Crucial regression assertion: msg2 (the next email) must be selected, NOT msg3 (the next next email)
        Assert.True(itemsAfter[0].ClassList.Contains("mud-selected-item"), "The next email (msg-2) must be selected after deleting msg-1");
        Assert.False(itemsAfter[1].ClassList.Contains("mud-selected-item"), "The next-next email (msg-3) must NOT be selected");
    }

    [Fact]
    public void EmailList_WhenMiddleEmailDeleted_CorrectlySelectsNextEmail()
    {
        // Arrange
        var msg1 = new EmailMessage { Id = "msg-1", Subject = "Email One", FromAddress = "alice@example.com", Date = DateTimeOffset.UtcNow };
        var msg2 = new EmailMessage { Id = "msg-2", Subject = "Email Two", FromAddress = "bob@example.com", Date = DateTimeOffset.UtcNow };
        var msg3 = new EmailMessage { Id = "msg-3", Subject = "Email Three", FromAddress = "carol@example.com", Date = DateTimeOffset.UtcNow };

        var initialList = new List<EmailMessage> { msg1, msg2, msg3 };

        // Act - initial render with msg2 selected
        var cut = Render<EmailList>(parameters => parameters
            .Add(p => p.Messages, initialList)
            .Add(p => p.SelectedMessageId, "msg-2"));

        var itemsBefore = cut.FindAll(".mud-list-item");
        Assert.Equal(3, itemsBefore.Count);
        Assert.False(itemsBefore[0].ClassList.Contains("mud-selected-item"));
        Assert.True(itemsBefore[1].ClassList.Contains("mud-selected-item"));
        Assert.False(itemsBefore[2].ClassList.Contains("mud-selected-item"));

        // Act - simulate deleting msg-2: list now has [msg1, msg3], selected is msg3
        var updatedList = new List<EmailMessage> { msg1, msg3 };
        cut.Render(parameters => parameters
            .Add(p => p.Messages, updatedList)
            .Add(p => p.SelectedMessageId, "msg-3"));

        // Assert
        var itemsAfter = cut.FindAll(".mud-list-item");
        Assert.Equal(2, itemsAfter.Count);
        Assert.Contains("Email One", itemsAfter[0].TextContent);
        Assert.Contains("Email Three", itemsAfter[1].TextContent);

        Assert.False(itemsAfter[0].ClassList.Contains("mud-selected-item"), "msg-1 should not be selected");
        Assert.True(itemsAfter[1].ClassList.Contains("mud-selected-item"), "msg-3 must be selected");
    }
}
