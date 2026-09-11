using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Linq;
using System.Text.Json;

namespace Spokes_Server.Tests.Core.Models
{
    public class BoardTests
    {
        [Fact]
        public void Board_Initialization_SetsDefaultsCorrectly()
        {
            var board = new Board();

            Assert.False(string.IsNullOrEmpty(board.Id));
            Assert.Equal("Untitled Board", board.Title);
            Assert.Null(board.Icon);
            Assert.Null(board.Description);
            Assert.True(board.CreatedAt <= DateTime.Now);
            Assert.True(board.UpdatedAt <= DateTime.Now);
            Assert.Equal(string.Empty, board.OwnerId);
            Assert.Empty(board.MemberIds);
            Assert.False(board.IsPublic);
            Assert.Empty(board.Properties);
            Assert.Empty(board.Views);
        }

        [Fact]
        public void BoardProperty_Initialization_SetsDefaultsCorrectly()
        {
            var prop = new BoardProperty();

            Assert.False(string.IsNullOrEmpty(prop.Id));
            Assert.Equal("New Property", prop.Name);
            Assert.Equal(BoardPropertyType.Text, prop.Type);
            Assert.Empty(prop.Options);
        }

        [Fact]
        public void BoardPropertyOption_Initialization_SetsDefaultsCorrectly()
        {
            var option = new BoardPropertyOption();

            Assert.False(string.IsNullOrEmpty(option.Id));
            Assert.Equal("Option", option.Name);
            Assert.Equal("gray", option.Color);
        }

        [Fact]
        public void BoardView_Initialization_SetsDefaultsCorrectly()
        {
            var view = new BoardView();

            Assert.False(string.IsNullOrEmpty(view.Id));
            Assert.Equal("New View", view.Name);
            Assert.Equal(BoardViewType.Kanban, view.Type);
            Assert.Null(view.GroupByPropertyId);
            Assert.Null(view.DatePropertyId);
            Assert.Empty(view.VisiblePropertyIds);
            Assert.Empty(view.Filters);
            Assert.Empty(view.SortOptions);
            Assert.Empty(view.ColumnCalculations);
        }

        [Fact]
        public void BoardViewFilter_Initialization_SetsDefaultsCorrectly()
        {
            var filter = new BoardViewFilter();

            Assert.False(string.IsNullOrEmpty(filter.Id));
            Assert.Equal(string.Empty, filter.PropertyId);
            Assert.Equal(BoardFilterOperator.Contains, filter.Operator);
            Assert.Equal(string.Empty, filter.Value);
        }

        [Fact]
        public void BoardViewSortOption_Initialization_SetsDefaultsCorrectly()
        {
            var sort = new BoardViewSortOption();

            Assert.Equal(string.Empty, sort.PropertyId);
            Assert.False(sort.IsDescending);
        }

        [Fact]
        public void Serialization_Deserialization_PreservesEnumsAsStrings()
        {
            var board = new Board
            {
                Id = "b1",
                Title = "Test Board",
                Properties = new()
                {
                    new BoardProperty { Name = "Status", Type = BoardPropertyType.Select }
                },
                Views = new()
                {
                    new BoardView
                    {
                        Type = BoardViewType.Table,
                        Filters = new()
                        {
                            new BoardViewFilter { Operator = BoardFilterOperator.IsNotEmpty }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(board);

            Assert.Contains("\"Select\"", json);
            Assert.Contains("\"Table\"", json);
            Assert.Contains("\"IsNotEmpty\"", json);

            var deserialized = JsonSerializer.Deserialize<Board>(json);

            Assert.NotNull(deserialized);
            Assert.Single(deserialized.Properties);
            Assert.Equal(BoardPropertyType.Select, deserialized.Properties.First().Type);

            Assert.Single(deserialized.Views);
            Assert.Equal(BoardViewType.Table, deserialized.Views.First().Type);

            Assert.Single(deserialized.Views.First().Filters);
            Assert.Equal(BoardFilterOperator.IsNotEmpty, deserialized.Views.First().Filters.First().Operator);
        }
    }
}


