using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
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


namespace Spokes_Server.Core.Services.Projects;

public class BoardService
{
    private readonly BoardRepository _boards;
    private readonly BoardCardRepository _boardCards;
    private readonly BoardTemplateRepository _boardTemplates;

    public BoardService(BoardRepository boards, BoardCardRepository boardCards, BoardTemplateRepository boardTemplates)
    {
        _boards = boards;
        _boardCards = boardCards;
        _boardTemplates = boardTemplates;
    }

    // --- BOARD OPERATIONS ---
    public Board CreateBoard(string title, string? description = null, string? icon = null, string ownerId = "")
    {
        var board = new Board
        {
            Title = title,
            Description = description,
            Icon = icon,
            OwnerId = ownerId,
            MemberIds = !string.IsNullOrEmpty(ownerId) ? new List<string> { ownerId } : new List<string>() // Add owner as member by default? Usually yes.
        };

        // Add default "Status" property
        var titleProp = new BoardProperty { Name = "Name", Type = BoardPropertyType.Text }; // Invisible main prop, but good to have tracked if we want

        var statusProp = new BoardProperty
        {
            Name = "Status",
            Type = BoardPropertyType.Select,
            Options = new List<BoardPropertyOption>
            {
                new() { Name = "To Do", Color = "default" },
                new() { Name = "In Progress", Color = "info" },
                new() { Name = "Done", Color = "success" }
            }
        };

        board.Properties.Add(statusProp);

        // Add Default Kanban View
        var kanbanView = new BoardView
        {
            Name = "Board View",
            Type = BoardViewType.Kanban,
            GroupByPropertyId = statusProp.Id
        };
        board.Views.Add(kanbanView);

        // Add Default Table View
        board.Views.Add(new BoardView { Name = "Table View", Type = BoardViewType.Table });

        _boards.Save(board);
        return board;
    }

    public void UpdateBoard(Board board)
    {
        _boards.Save(board);
    }

    public void DeleteBoard(string boardId)
    {
        // 1. Delete all cards
        var cards = _boardCards.GetByBoardId(boardId);
        foreach (var card in cards)
        {
            _boardCards.Delete(card.Id);
        }

        // 2. Delete board
        _boards.Delete(boardId);
    }

    // --- CARD OPERATIONS ---
    public BoardCard CreateCard(string boardId, string title)
    {
        var card = new BoardCard
        {
            BoardId = boardId,
            Title = title
        };

        // Initialize default properties?
        // Maybe find the "Status" property and set it to the first option?
        var board = _boards.GetById(boardId);
        if (board != null)
        {
            var statusProp = board.Properties.FirstOrDefault(p => p.Name == "Status");
            if (statusProp != null && statusProp.Options.Any())
            {
                card.PropertyValues[statusProp.Id] = statusProp.Options.First().Id;
            }
        }

        _boardCards.Save(card);
        return card;
    }

    public void UpdateCard(BoardCard card)
    {
        card.UpdatedAt = DateTime.Now;
        _boardCards.Save(card);
    }

    public void DeleteCard(string cardId)
    {
        _boardCards.Delete(cardId);
    }

    // --- PROPERTY OPERATIONS ---

    /// <summary>
    /// Update a specific property value for a card. 
    /// Handles logic like "if this is a select property, ensure the option exists" if needed.
    /// </summary>
    public void UpdateCardProperty(string cardId, string propertyId, string value)
    {
        var card = _boardCards.GetById(cardId);
        if (card == null) return;

        card.PropertyValues[propertyId] = value;
        UpdateCard(card);
    }
    // --- COMMENTS & ACTIVITY ---
    public void AddComment(string cardId, string userId, string text)
    {
        var card = _boardCards.GetById(cardId);
        if (card == null) return;

        var comment = new BoardCardComment
        {
            CardId = cardId,
            UserId = userId,
            Text = text,
            CreatedAt = DateTime.Now
        };
        card.Comments.Add(comment);

        LogActivity(card.Id, userId, "commented", text);
        UpdateCard(card);
    }

    public void LogActivity(string cardId, string userId, string action, string details)
    {
        var card = _boardCards.GetById(cardId);
        if (card == null) return;

        var activity = new BoardCardActivity
        {
            CardId = cardId,
            UserId = userId,
            Action = action,
            Details = details,
            CreatedAt = DateTime.Now
        };
        card.ActivityLog.Insert(0, activity); // Newest first
        UpdateCard(card);
    }
    // --- TEMPLATES & PORTABILITY ---

    // --- TEMPLATES & PORTABILITY ---

    public List<string> GetTemplates()
    {
        // System Templates
        var list = new List<string> { "Empty", "Kanban", "Roadmap", "Simple CRM" };

        // Custom Templates
        var custom = _boardTemplates.GetAll().Select(t => t.Name).ToList();
        list.AddRange(custom);

        return list;
    }

    public void SaveAsTemplate(Board board, string templateName)
    {
        var template = new BoardTemplate
        {
            Name = templateName,
            Description = $"Created from board '{board.Title}'",
            Icon = board.Icon,
            Properties = board.Properties, // Serialization will handle deep clone behavior effectively for templates
            Views = board.Views
        };

        // Serialize/Deserialize to ensure deep clone and no reference sharing
        var json = System.Text.Json.JsonSerializer.Serialize(template);
        var clone = System.Text.Json.JsonSerializer.Deserialize<BoardTemplate>(json);

        if (clone != null)
        {
            clone.Id = Guid.NewGuid().ToString();
            clone.Name = templateName;
            _boardTemplates.Save(clone);
        }
    }

    public Board CreateBoardFromTemplate(string title, string templateName, string ownerId = "")
    {
        // 1. Check for Custom Template first
        var customTemplate = _boardTemplates.GetAll().FirstOrDefault(t => t.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));

        if (customTemplate != null)
        {
            var board = new Board
            {
                Title = title,
                Icon = customTemplate.Icon,
                OwnerId = ownerId,
                MemberIds = !string.IsNullOrEmpty(ownerId) ? new List<string> { ownerId } : new List<string>()
            };

            // Deep Clone logic: We use JSON serialization to avoid reference issues
            var json = System.Text.Json.JsonSerializer.Serialize(customTemplate);
            var tempClone = System.Text.Json.JsonSerializer.Deserialize<BoardTemplate>(json);

            if (tempClone != null)
            {
                // 1. Map Old IDs -> New IDs to preserve View-Property relationships
                var map = new Dictionary<string, string>();

                foreach (var p in tempClone.Properties)
                {
                    var newId = Guid.NewGuid().ToString();
                    map[p.Id] = newId;
                    p.Id = newId;

                    foreach (var o in p.Options)
                    {
                        o.Id = Guid.NewGuid().ToString();
                    }
                }

                foreach (var v in tempClone.Views)
                {
                    v.Id = Guid.NewGuid().ToString();

                    if (!string.IsNullOrEmpty(v.GroupByPropertyId) && map.ContainsKey(v.GroupByPropertyId))
                    {
                        v.GroupByPropertyId = map[v.GroupByPropertyId];
                    }

                    v.VisiblePropertyIds = v.VisiblePropertyIds.Select(id => map.ContainsKey(id) ? map[id] : id).ToList();
                }

                board.Properties = tempClone.Properties;
                board.Views = tempClone.Views;
            }

            _boards.Save(board);
            return board;
        }

        // 2. Create Base Board (System Defaults)
        var boardSys = CreateBoard(title, null, null, ownerId);

        // 3. Customize based on Template
        if (templateName == "Roadmap")
        {
            boardSys.Properties.Clear();
            boardSys.Views.Clear();

            var status = new BoardProperty
            {
                Name = "Status",
                Type = BoardPropertyType.Select,
                Options = new()
                {
                    new() { Name = "Not Started", Color = "default" },
                    new() { Name = "In Progress", Color = "blue" },
                    new() { Name = "Launched", Color = "green" }
                }
            };
            var priority = new BoardProperty
            {
                Name = "Priority",
                Type = BoardPropertyType.Select,
                Options = new()
                {
                    new() { Name = "High", Color = "red" },
                    new() { Name = "Medium", Color = "orange" },
                    new() { Name = "Low", Color = "default" }
                }
            };

            boardSys.Properties.Add(status);
            boardSys.Properties.Add(priority);

            boardSys.Views.Add(new BoardView { Name = "By Status", Type = BoardViewType.Kanban, GroupByPropertyId = status.Id });
            boardSys.Views.Add(new BoardView { Name = "By Priority", Type = BoardViewType.Kanban, GroupByPropertyId = priority.Id });
            boardSys.Views.Add(new BoardView { Name = "All Tasks", Type = BoardViewType.Table });
        }
        else if (templateName == "Simple CRM")
        {
            boardSys.Properties.Clear();
            boardSys.Views.Clear();

            var stage = new BoardProperty
            {
                Name = "Stage",
                Type = BoardPropertyType.Select,
                Options = new()
                {
                    new() { Name = "Lead", Color = "default" },
                    new() { Name = "Negotiation", Color = "orange" },
                    new() { Name = "Closed Won", Color = "green" },
                    new() { Name = "Closed Lost", Color = "red" }
                }
            };
            var email = new BoardProperty { Name = "Email", Type = BoardPropertyType.Email };
            var phone = new BoardProperty { Name = "Phone", Type = BoardPropertyType.Phone };
            var value = new BoardProperty { Name = "Deal Value", Type = BoardPropertyType.Number };

            boardSys.Properties.Add(stage);
            boardSys.Properties.Add(email);
            boardSys.Properties.Add(phone);
            boardSys.Properties.Add(value);

            boardSys.Views.Add(new BoardView { Name = "Pipeline", Type = BoardViewType.Kanban, GroupByPropertyId = stage.Id });
            boardSys.Views.Add(new BoardView { Name = "List", Type = BoardViewType.Table });
        }
        else if (templateName == "Empty")
        {
            boardSys.Properties.Clear();
            boardSys.Views.Clear();
            boardSys.Views.Add(new BoardView { Name = "Table", Type = BoardViewType.Table });
        }

        UpdateBoard(boardSys);
        return boardSys;
    }

    public string ExportBoardToJson(string boardId)
    {
        var board = _boards.GetById(boardId);
        if (board == null) return "{}";

        var cards = _boardCards.GetByBoardId(boardId);

        var export = new BoardExport
        {
            Board = board,
            Cards = cards
        };

        return System.Text.Json.JsonSerializer.Serialize(export, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public Board? ImportBoardFromJson(string json, string ownerId = "")
    {
        try
        {
            var export = System.Text.Json.JsonSerializer.Deserialize<BoardExport>(json);
            if (export?.Board == null) throw new Exception("Invalid Board JSON");

            var oldBoard = export.Board;

            // Create New Board (Clone)
            var newBoard = oldBoard;
            newBoard.Id = Guid.NewGuid().ToString();
            newBoard.Title += " (Imported)";
            newBoard.CreatedAt = DateTime.Now;
            newBoard.UpdatedAt = DateTime.Now;
            newBoard.OwnerId = ownerId;
            newBoard.MemberIds = !string.IsNullOrEmpty(ownerId) ? new List<string> { ownerId } : new List<string>();

            _boards.Save(newBoard);

            // Cards
            if (export.Cards != null)
            {
                foreach (var card in export.Cards)
                {
                    card.Id = Guid.NewGuid().ToString();
                    card.BoardId = newBoard.Id;

                    if (card.Comments != null)
                        card.Comments.ForEach(c => c.Id = Guid.NewGuid().ToString());

                    if (card.ActivityLog != null)
                        card.ActivityLog.ForEach(a => a.Id = Guid.NewGuid().ToString());

                    _boardCards.Save(card);
                }
            }

            return newBoard;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Import Failed: {ex.Message}");
            return null;
        }
    }

    public class BoardExport
    {
        public Board Board { get; set; } = default!;
        public List<BoardCard> Cards { get; set; } = new();
    }
}



