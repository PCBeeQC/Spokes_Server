using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Pages.Boards;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Storage;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Boards;

/// <summary>
/// bUnit tests validating interactive Razor component DOM rendering, column generation,
/// parameter cascades, and events for KanbanBoard and TableBoard components.
/// </summary>
public class BoardInteractiveRenderingTests : BunitContext
{
    private readonly string _testDataDir;
    private readonly Database _db;
    private readonly BoardService _boardService;

    public BoardInteractiveRenderingTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_BoardBunit_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        Services.AddSingleton<IConfiguration>(mockConfig.Object);

        Services.AddLogging();
        Services.AddSingleton<EncryptionService>();
        Services.AddSpokesDatabase();
        Services.AddSingleton<BoardService>();

        _db = Services.GetRequiredService<Database>();
        _boardService = Services.GetRequiredService<BoardService>();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_testDataDir))
            {
                try
                {
                    Directory.Delete(_testDataDir, true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void KanbanBoard_WhenNoSelectPropertiesAndGroupByUnset_RendersAlertPrompt()
    {
        // Arrange
        var board = new Board
        {
            Id = "board-empty",
            Title = "Empty Board",
            Properties = new List<BoardProperty>()
        };
        var view = new BoardView
        {
            Id = "view-empty",
            Name = "Kanban",
            Type = BoardViewType.Kanban,
            GroupByPropertyId = string.Empty
        };

        // Act
        var cut = Render<KanbanBoard>(parameters => parameters
            .Add(p => p.Board, board)
            .Add(p => p.View, view)
            .Add(p => p.Cards, new List<BoardCard>()));

        // Assert
        Assert.Contains("Please select a property to group by", cut.Markup);
    }

    [Fact]
    public void KanbanBoard_WhenConfiguredWithSelectProperty_RendersColumnsAndCards()
    {
        // Arrange
        var statusProp = new BoardProperty
        {
            Id = "prop-status",
            Name = "Status",
            Type = BoardPropertyType.Select,
            Options = new List<BoardPropertyOption>
            {
                new() { Id = "opt-todo", Name = "To Do", Color = "gray" },
                new() { Id = "opt-done", Name = "Done", Color = "green" }
            }
        };
        var board = new Board
        {
            Id = "board-1",
            Title = "Dev Board",
            Properties = new List<BoardProperty> { statusProp }
        };
        var view = new BoardView
        {
            Id = "view-1",
            Name = "Kanban",
            Type = BoardViewType.Kanban,
            GroupByPropertyId = "prop-status",
            VisiblePropertyIds = new List<string> { "prop-status" }
        };
        var card1 = new BoardCard
        {
            Id = "card-1",
            BoardId = "board-1",
            Title = "Task Alpha",
            PropertyValues = new Dictionary<string, string> { { "prop-status", "opt-todo" } }
        };
        var card2 = new BoardCard
        {
            Id = "card-2",
            BoardId = "board-1",
            Title = "Task Beta",
            PropertyValues = new Dictionary<string, string> { { "prop-status", "opt-done" } }
        };
        var card3 = new BoardCard
        {
            Id = "card-3",
            BoardId = "board-1",
            Title = "Task Untracked",
            PropertyValues = new Dictionary<string, string>()
        };

        // Act
        var cut = Render<KanbanBoard>(parameters => parameters
            .Add(p => p.Board, board)
            .Add(p => p.View, view)
            .Add(p => p.Cards, new List<BoardCard> { card1, card2, card3 }));

        // Assert
        var markup = cut.Markup;
        Assert.Contains("Group By:", markup);
        Assert.Contains("No Status", markup);
        Assert.Contains("To Do", markup);
        Assert.Contains("Done", markup);
        Assert.Contains("Task Alpha", markup);
        Assert.Contains("Task Beta", markup);
        Assert.Contains("Task Untracked", markup);
    }

    [Fact]
    public void KanbanBoard_WhenConfiguredWithPersonProperty_RendersEmployeeColumns()
    {
        // Arrange
        var employee = new Employee
        {
            Id = "emp-42",
            FirstName = "Ada",
            LastName = "Lovelace",
            IsActive = true
        };
        _db.Employees.Save(employee);

        var assigneeProp = new BoardProperty
        {
            Id = "prop-assignee",
            Name = "Assignee",
            Type = BoardPropertyType.Person
        };
        var board = new Board
        {
            Id = "board-2",
            Title = "HR Board",
            Properties = new List<BoardProperty> { assigneeProp }
        };
        var view = new BoardView
        {
            Id = "view-2",
            Name = "Kanban",
            Type = BoardViewType.Kanban,
            GroupByPropertyId = "prop-assignee",
            VisiblePropertyIds = new List<string> { "prop-assignee" }
        };
        var card = new BoardCard
        {
            Id = "card-assignee",
            BoardId = "board-2",
            Title = "Onboard Engineer",
            PropertyValues = new Dictionary<string, string> { { "prop-assignee", "emp-42" } }
        };

        // Act
        var cut = Render<KanbanBoard>(parameters => parameters
            .Add(p => p.Board, board)
            .Add(p => p.View, view)
            .Add(p => p.Cards, new List<BoardCard> { card }));

        // Assert
        var markup = cut.Markup;
        Assert.Contains("Ada Lovelace", markup);
        Assert.Contains("Onboard Engineer", markup);
    }

    [Fact]
    public void KanbanBoard_WhenCreateCardClicked_AddsNewCardToCollection()
    {
        // Arrange
        var board = _boardService.CreateBoard("Sprint Board", "Desc", "icon", "user-1");
        var statusProp = board.Properties.First(p => p.Type == BoardPropertyType.Select);
        var view = new BoardView
        {
            Id = "view-sprint",
            Name = "Sprint Kanban",
            Type = BoardViewType.Kanban,
            GroupByPropertyId = statusProp.Id
        };
        var cardsList = new List<BoardCard>();

        // Act
        var cut = Render<KanbanBoard>(parameters => parameters
            .Add(p => p.Board, board)
            .Add(p => p.View, view)
            .Add(p => p.Cards, cardsList));

        var newButtons = cut.FindAll("button").Where(b => b.TextContent.Trim() == "New").ToList();
        Assert.NotEmpty(newButtons);
        newButtons[0].Click();

        // Assert
        Assert.Single(cardsList);
        Assert.Equal("New Card", cardsList[0].Title);
    }

    [Fact]
    public void TableBoard_RendersColumnsAndCardData()
    {
        // Arrange
        var statusProp = new BoardProperty
        {
            Id = "prop-status",
            Name = "Status",
            Type = BoardPropertyType.Select,
            Options = new List<BoardPropertyOption>
            {
                new() { Id = "opt-prog", Name = "In Progress", Color = "orange" }
            }
        };
        var board = new Board
        {
            Id = "board-table",
            Title = "Table Test Board",
            Properties = new List<BoardProperty> { statusProp }
        };
        var view = new BoardView
        {
            Id = "view-table",
            Name = "Table View",
            Type = BoardViewType.Table,
            VisiblePropertyIds = new List<string> { "prop-status" }
        };
        var card = new BoardCard
        {
            Id = "card-tbl-1",
            BoardId = "board-table",
            Title = "Feature XYZ",
            PropertyValues = new Dictionary<string, string> { { "prop-status", "opt-prog" } }
        };

        // Act
        var cut = Render<TableBoard>(parameters => parameters
            .Add(p => p.Board, board)
            .Add(p => p.View, view)
            .Add(p => p.Cards, new List<BoardCard> { card }));

        // Assert
        var markup = cut.Markup;
        Assert.Contains("Feature XYZ", markup);
        Assert.Contains("In Progress", markup);
    }
}
