using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;

namespace Spokes_Server.Tests.Core.Services.Projects;

public class BoardServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly BoardService _service;
    private readonly BoardRepository _boards;
    private readonly BoardCardRepository _boardCards;
    private readonly BoardTemplateRepository _boardTemplates;

    public BoardServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Board_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _boards = new BoardRepository(_persistence, _config);
            _boardCards = new BoardCardRepository(_persistence, _config);
            _boardTemplates = new BoardTemplateRepository(_persistence, _config);

            _service = new BoardService(_boards, _boardCards, _boardTemplates);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        [Fact]
        public void CreateBoard_InitializesWithDefaults()
        {
            var board = _service.CreateBoard("Test Board", "Description", "icon-class", "user-1");

            Assert.NotNull(board.Id);
            Assert.Equal("Test Board", board.Title);
            Assert.Equal("Description", board.Description);
            Assert.Equal("icon-class", board.Icon);
            Assert.Equal("user-1", board.OwnerId);
            Assert.Contains("user-1", board.MemberIds);

            // Default properties (Status)
            Assert.Contains(board.Properties, p => p.Name == "Status" && p.Type == BoardPropertyType.Select);

            // Default views (Kanban, Table)
            Assert.Equal(2, board.Views.Count);
            Assert.Contains(board.Views, v => v.Type == BoardViewType.Kanban);
            Assert.Contains(board.Views, v => v.Type == BoardViewType.Table);
        }

        [Fact]
        public void DeleteBoard_RemovesBoardAndCards()
        {
            var board = _service.CreateBoard("To Delete");
            var card = _service.CreateCard(board.Id, "To Delete Card");

            _service.DeleteBoard(board.Id);

            Assert.Null(_boards.GetById(board.Id));
            Assert.Empty(_boardCards.GetByBoardId(board.Id));
        }

        [Fact]
        public void CreateCard_InitializesWithDefaultStatus()
        {
            var board = _service.CreateBoard("Board");
            var statusProp = board.Properties.First(p => p.Name == "Status");
            var firstOptionId = statusProp.Options.First().Id;

            var card = _service.CreateCard(board.Id, "Card Title");

            Assert.Equal(board.Id, card.BoardId);
            Assert.Equal("Card Title", card.Title);
            Assert.Equal(firstOptionId, card.PropertyValues[statusProp.Id]);
        }

        [Fact]
        public void UpdateCardProperty_UpdatesValue()
        {
            var board = _service.CreateBoard("Board");
            var card = _service.CreateCard(board.Id, "Card");
            var propId = "custom-prop";

            _service.UpdateCardProperty(card.Id, propId, "new-value");

            var updatedCard = _boardCards.GetById(card.Id);
            Assert.Equal("new-value", updatedCard.PropertyValues[propId]);
        }

        [Fact]
        public void AddComment_AndLogActivity_Works()
        {
            var board = _service.CreateBoard("Board");
            var card = _service.CreateCard(board.Id, "Card");

            _service.AddComment(card.Id, "user-1", "Test Comment");

            var updatedCard = _boardCards.GetById(card.Id);
            Assert.Single(updatedCard.Comments);
            Assert.Equal("Test Comment", updatedCard.Comments[0].Text);

            // Activity log check
            Assert.Contains(updatedCard.ActivityLog, a => a.Action == "commented" && a.Details == "Test Comment");
        }

        [Fact]
        public void Templates_SaveAndRetrieve()
        {
            var board = _service.CreateBoard("Board");
            _service.SaveAsTemplate(board, "My Template");

            var templates = _service.GetTemplates();
            Assert.Contains("My Template", templates);
        }

        [Fact]
        public void CreateBoardFromTemplate_SystemRoadmap()
        {
            var board = _service.CreateBoardFromTemplate("My Roadmap", "Roadmap", "user-1");

            Assert.Equal("My Roadmap", board.Title);
            Assert.Contains(board.Properties, p => p.Name == "Status");
            Assert.Contains(board.Properties, p => p.Name == "Priority");
            Assert.Equal(3, board.Views.Count); // 2 Kanban, 1 Table
        }

        [Fact]
        public void CreateBoardFromTemplate_CustomTemplate_ClonesCorrectlty()
        {
            var sourceBoard = _service.CreateBoard("Source");
            _service.SaveAsTemplate(sourceBoard, "Custom");

            var newBoard = _service.CreateBoardFromTemplate("Cloned", "Custom", "user-2");

            Assert.Equal("Cloned", newBoard.Title);
            Assert.NotEqual(sourceBoard.Id, newBoard.Id);
            Assert.Equal(sourceBoard.Properties.Count, newBoard.Properties.Count);
            // Verify IDs changed
            Assert.NotEqual(sourceBoard.Properties[0].Id, newBoard.Properties[0].Id);
        }

        [Fact]
        public void ExportAndImport_PreservesData()
        {
            var board = _service.CreateBoard("Export Board");
            var card = _service.CreateCard(board.Id, "Export Card");
            _service.AddComment(card.Id, "user-1", "Comment to export");

            var json = _service.ExportBoardToJson(board.Id);
            var importedBoard = _service.ImportBoardFromJson(json, "user-2");

            Assert.NotNull(importedBoard);
            Assert.Equal("Export Board (Imported)", importedBoard.Title);
            Assert.Equal("user-2", importedBoard.OwnerId);

            var importedCards = _boardCards.GetByBoardId(importedBoard.Id);
            Assert.Single(importedCards);
            Assert.Equal("Export Card", importedCards[0].Title);
            Assert.Single(importedCards[0].Comments);
            Assert.Equal("Comment to export", importedCards[0].Comments[0].Text);
        }

        [Fact]
        public void CreateBoardFromTemplate_SimpleCrm_ConfiguresStageAndFields()
        {
            var board = _service.CreateBoardFromTemplate("CRM Board", "Simple CRM", "user-crm");

            Assert.Equal("CRM Board", board.Title);
            Assert.Equal("user-crm", board.OwnerId);

            // Verify Stage (Select), Email, Phone, Deal Value (Number) properties
            Assert.Equal(4, board.Properties.Count);

            var stageProp = board.Properties.FirstOrDefault(p => p.Name == "Stage");
            Assert.NotNull(stageProp);
            Assert.Equal(BoardPropertyType.Select, stageProp.Type);
            Assert.Equal(4, stageProp.Options.Count);
            Assert.Contains(stageProp.Options, o => o.Name == "Lead");
            Assert.Contains(stageProp.Options, o => o.Name == "Negotiation");
            Assert.Contains(stageProp.Options, o => o.Name == "Closed Won");
            Assert.Contains(stageProp.Options, o => o.Name == "Closed Lost");

            var emailProp = board.Properties.FirstOrDefault(p => p.Name == "Email");
            Assert.NotNull(emailProp);
            Assert.Equal(BoardPropertyType.Email, emailProp.Type);

            var phoneProp = board.Properties.FirstOrDefault(p => p.Name == "Phone");
            Assert.NotNull(phoneProp);
            Assert.Equal(BoardPropertyType.Phone, phoneProp.Type);

            var valueProp = board.Properties.FirstOrDefault(p => p.Name == "Deal Value");
            Assert.NotNull(valueProp);
            Assert.Equal(BoardPropertyType.Number, valueProp.Type);

            // Verify Pipeline Kanban and List Table views
            Assert.Equal(2, board.Views.Count);

            var pipelineView = board.Views.FirstOrDefault(v => v.Name == "Pipeline");
            Assert.NotNull(pipelineView);
            Assert.Equal(BoardViewType.Kanban, pipelineView.Type);
            Assert.Equal(stageProp.Id, pipelineView.GroupByPropertyId);

            var listView = board.Views.FirstOrDefault(v => v.Name == "List");
            Assert.NotNull(listView);
            Assert.Equal(BoardViewType.Table, listView.Type);
        }

        [Fact]
        public void CreateBoardFromTemplate_Empty_ConfiguresOnlyTableView()
        {
            var board = _service.CreateBoardFromTemplate("Empty Board", "Empty", "user-empty");

            Assert.Equal("Empty Board", board.Title);
            Assert.Empty(board.Properties);
            Assert.Single(board.Views);
            Assert.Equal("Table", board.Views[0].Name);
            Assert.Equal(BoardViewType.Table, board.Views[0].Type);
        }

        [Fact]
        public void CreateBoardFromTemplate_CustomTemplate_MapsGroupByPropertyAndVisibleProperties()
        {
            var sourceBoard = _service.CreateBoard("Source Board");
            var statusProp = sourceBoard.Properties.First(p => p.Name == "Status");
            var kanbanView = sourceBoard.Views.First(v => v.Type == BoardViewType.Kanban);
            kanbanView.GroupByPropertyId = statusProp.Id;
            kanbanView.VisiblePropertyIds = new List<string> { statusProp.Id, "external-prop-id" };

            _service.SaveAsTemplate(sourceBoard, "Custom");

            var clonedBoard = _service.CreateBoardFromTemplate("Board", "Custom");

            var clonedStatusProp = clonedBoard.Properties.First(p => p.Name == "Status");
            var clonedKanbanView = clonedBoard.Views.First(v => v.Type == BoardViewType.Kanban);

            Assert.NotEqual(statusProp.Id, clonedStatusProp.Id);
            Assert.Equal(clonedStatusProp.Id, clonedKanbanView.GroupByPropertyId);
            Assert.Contains(clonedStatusProp.Id, clonedKanbanView.VisiblePropertyIds);
            Assert.DoesNotContain(statusProp.Id, clonedKanbanView.VisiblePropertyIds);
            Assert.Contains("external-prop-id", clonedKanbanView.VisiblePropertyIds);
        }

        [Fact]
        public void ExportBoardToJson_WhenBoardDoesNotExist_ReturnsEmptyJson()
        {
            var result = _service.ExportBoardToJson("non-existent-board-id");
            Assert.Equal("{}", result);
        }

        [Fact]
        public void ImportBoardFromJson_WhenJsonIsInvalidOrNull_ReturnsNull()
        {
            var resultInvalid = _service.ImportBoardFromJson("{ invalid-json-payload");
            Assert.Null(resultInvalid);

            var resultEmpty = _service.ImportBoardFromJson("{}");
            Assert.Null(resultEmpty);

            var resultNull = _service.ImportBoardFromJson("null");
            Assert.Null(resultNull);
        }

        [Fact]
        public void UpdateBoard_PersistsChanges()
        {
            var board = _service.CreateBoard("Original Title", "Original Description");
            board.Title = "Updated Title";
            board.Description = "Updated Description";

            _service.UpdateBoard(board);

            var persisted = _boards.GetById(board.Id);
            Assert.NotNull(persisted);
            Assert.Equal("Updated Title", persisted.Title);
            Assert.Equal("Updated Description", persisted.Description);
        }

        [Fact]
        public void DeleteCard_RemovesCard()
        {
            var board = _service.CreateBoard("Board");
            var card = _service.CreateCard(board.Id, "Card to delete");
            Assert.NotNull(_boardCards.GetById(card.Id));

            _service.DeleteCard(card.Id);

            Assert.Null(_boardCards.GetById(card.Id));
        }

        [Fact]
        public void UpdateCardProperty_WhenCardNotFound_ReturnsSafely()
        {
            var exception = Record.Exception(() => _service.UpdateCardProperty("non-existent-card-id", "prop-1", "value"));
            Assert.Null(exception);
        }

        [Fact]
        public void AddComment_WhenCardNotFound_ReturnsSafely()
        {
            var exception = Record.Exception(() => _service.AddComment("non-existent-card-id", "user-1", "comment"));
            Assert.Null(exception);
        }

        [Fact]
        public void LogActivity_WhenCardNotFound_ReturnsSafely()
        {
            var exception = Record.Exception(() => _service.LogActivity("non-existent-card-id", "user-1", "action", "details"));
            Assert.Null(exception);
        }

        [Fact]
        public void UpdateCard_PersistsChanges()
        {
            var board = _service.CreateBoard("Board");
            var card = _service.CreateCard(board.Id, "Initial Card");

            card.Title = "Updated Card Title";
            _service.UpdateCard(card);

            var reloaded = _boardCards.GetById(card.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("Updated Card Title", reloaded.Title);
        }
    }
