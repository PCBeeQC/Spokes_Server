using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spokes_Server.Tests.Core.Services.Projects
{
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
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Board_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
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
    }
}



