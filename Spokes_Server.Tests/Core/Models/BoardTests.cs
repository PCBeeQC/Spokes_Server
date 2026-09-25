using Spokes_Server.Core.Models.Projects;
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

        [Fact]
        public void Board_Equals_ReturnsTrueForReferenceEquality()
        {
            var board = new Board { Id = "board-1" };
            Assert.True(board.Equals(board));
        }

        [Fact]
        public void Board_Equals_ReturnsTrueForSameId()
        {
            var board1 = new Board { Id = "board-1", Title = "Board 1" };
            var board2 = new Board { Id = "board-1", Title = "Board 2" };

            Assert.True(board1.Equals(board2));
            Assert.True(board2.Equals(board1));
        }

        [Fact]
        public void Board_Equals_ReturnsFalseForDifferentId()
        {
            var board1 = new Board { Id = "board-1" };
            var board2 = new Board { Id = "board-2" };

            Assert.False(board1.Equals(board2));
            Assert.False(board2.Equals(board1));
        }

        [Fact]
        public void Board_Equals_ReturnsFalseForNull()
        {
            var board = new Board { Id = "board-1" };
            Assert.False(board.Equals(null));
        }

        [Fact]
        public void Board_Equals_ReturnsFalseForDifferentType()
        {
            var board = new Board { Id = "board-1" };
            Assert.False(board.Equals("not-a-board"));
            Assert.False(board.Equals(new object()));
        }

        [Fact]
        public void Board_GetHashCode_SameIdProducesSameHashCode()
        {
            var board1 = new Board { Id = "board-1", Title = "A" };
            var board2 = new Board { Id = "board-1", Title = "B" };

            Assert.Equal(board1.GetHashCode(), board2.GetHashCode());
        }

        [Fact]
        public void Board_GetHashCode_NullIdFallbackDoesNotThrow()
        {
            var board = new Board { Id = null! };
            var exception = Record.Exception(() => board.GetHashCode());
            Assert.Null(exception);
        }

        [Fact]
        public void Board_PropertyMutations_CanSetAndGetProperties()
        {
            var now = DateTime.UtcNow;
            var memberIds = new List<string> { "emp-1", "emp-2" };
            var properties = new List<BoardProperty> { new BoardProperty { Id = "prop-1" } };
            var views = new List<BoardView> { new BoardView { Id = "view-1" } };

            var board = new Board
            {
                Id = "custom-id",
                Title = "Custom Title",
                Icon = "mdi-icon",
                Description = "Custom Description",
                CreatedAt = now,
                UpdatedAt = now.AddMinutes(5),
                OwnerId = "emp-owner",
                MemberIds = memberIds,
                IsPublic = true,
                Properties = properties,
                Views = views
            };

            Assert.Equal("custom-id", board.Id);
            Assert.Equal("Custom Title", board.Title);
            Assert.Equal("mdi-icon", board.Icon);
            Assert.Equal("Custom Description", board.Description);
            Assert.Equal(now, board.CreatedAt);
            Assert.Equal(now.AddMinutes(5), board.UpdatedAt);
            Assert.Equal("emp-owner", board.OwnerId);
            Assert.Same(memberIds, board.MemberIds);
            Assert.True(board.IsPublic);
            Assert.Same(properties, board.Properties);
            Assert.Same(views, board.Views);
        }

        [Fact]
        public void BoardProperty_PropertyMutations_CanSetAndGetProperties()
        {
            var options = new List<BoardPropertyOption> { new BoardPropertyOption { Id = "opt-1" } };
            var prop = new BoardProperty
            {
                Id = "prop-123",
                Name = "Priority",
                Type = BoardPropertyType.Select,
                Options = options
            };

            Assert.Equal("prop-123", prop.Id);
            Assert.Equal("Priority", prop.Name);
            Assert.Equal(BoardPropertyType.Select, prop.Type);
            Assert.Same(options, prop.Options);
        }

        [Fact]
        public void BoardPropertyOption_PropertyMutations_CanSetAndGetProperties()
        {
            var option = new BoardPropertyOption
            {
                Id = "opt-123",
                Name = "High",
                Color = "#ff0000"
            };

            Assert.Equal("opt-123", option.Id);
            Assert.Equal("High", option.Name);
            Assert.Equal("#ff0000", option.Color);
        }

        [Fact]
        public void BoardView_PropertyMutations_CanSetAndGetProperties()
        {
            var visibleProps = new List<string> { "prop-1", "prop-2" };
            var filters = new List<BoardViewFilter> { new BoardViewFilter { Id = "f-1" } };
            var sortOptions = new List<BoardViewSortOption> { new BoardViewSortOption { PropertyId = "prop-1", IsDescending = true } };
            var calculations = new Dictionary<string, string> { { "prop-2", "sum" } };

            var view = new BoardView
            {
                Id = "view-123",
                Name = "Calendar View",
                Type = BoardViewType.Calendar,
                GroupByPropertyId = "prop-select",
                DatePropertyId = "prop-date",
                VisiblePropertyIds = visibleProps,
                Filters = filters,
                SortOptions = sortOptions,
                ColumnCalculations = calculations
            };

            Assert.Equal("view-123", view.Id);
            Assert.Equal("Calendar View", view.Name);
            Assert.Equal(BoardViewType.Calendar, view.Type);
            Assert.Equal("prop-select", view.GroupByPropertyId);
            Assert.Equal("prop-date", view.DatePropertyId);
            Assert.Same(visibleProps, view.VisiblePropertyIds);
            Assert.Same(filters, view.Filters);
            Assert.Same(sortOptions, view.SortOptions);
            Assert.Same(calculations, view.ColumnCalculations);
        }

        [Fact]
        public void BoardViewFilter_PropertyMutations_CanSetAndGetProperties()
        {
            var filter = new BoardViewFilter
            {
                Id = "filter-123",
                PropertyId = "status-prop",
                Operator = BoardFilterOperator.IsNot,
                Value = "Done"
            };

            Assert.Equal("filter-123", filter.Id);
            Assert.Equal("status-prop", filter.PropertyId);
            Assert.Equal(BoardFilterOperator.IsNot, filter.Operator);
            Assert.Equal("Done", filter.Value);
        }

        [Fact]
        public void BoardViewSortOption_PropertyMutations_CanSetAndGetProperties()
        {
            var sort = new BoardViewSortOption
            {
                PropertyId = "due-date",
                IsDescending = true
            };

            Assert.Equal("due-date", sort.PropertyId);
            Assert.True(sort.IsDescending);
        }

        [Theory]
        [InlineData(BoardPropertyType.Text)]
        [InlineData(BoardPropertyType.Select)]
        [InlineData(BoardPropertyType.MultiSelect)]
        [InlineData(BoardPropertyType.Person)]
        [InlineData(BoardPropertyType.Date)]
        [InlineData(BoardPropertyType.Checkbox)]
        [InlineData(BoardPropertyType.URL)]
        [InlineData(BoardPropertyType.Email)]
        [InlineData(BoardPropertyType.Phone)]
        [InlineData(BoardPropertyType.Number)]
        [InlineData(BoardPropertyType.CreatedBy)]
        [InlineData(BoardPropertyType.UpdatedBy)]
        [InlineData(BoardPropertyType.CreatedAt)]
        [InlineData(BoardPropertyType.UpdatedAt)]
        [InlineData(BoardPropertyType.Button)]
        [InlineData(BoardPropertyType.Project)]
        public void BoardPropertyType_AllValues_SerializeAndDeserializeAsString(BoardPropertyType propertyType)
        {
            var json = JsonSerializer.Serialize(propertyType);
            Assert.Equal($"\"{propertyType}\"", json);

            var deserialized = JsonSerializer.Deserialize<BoardPropertyType>(json);
            Assert.Equal(propertyType, deserialized);
        }

        [Theory]
        [InlineData(BoardViewType.Kanban)]
        [InlineData(BoardViewType.Table)]
        [InlineData(BoardViewType.Gallery)]
        [InlineData(BoardViewType.Calendar)]
        public void BoardViewType_AllValues_SerializeAndDeserializeAsString(BoardViewType viewType)
        {
            var json = JsonSerializer.Serialize(viewType);
            Assert.Equal($"\"{viewType}\"", json);

            var deserialized = JsonSerializer.Deserialize<BoardViewType>(json);
            Assert.Equal(viewType, deserialized);
        }

        [Theory]
        [InlineData(BoardFilterOperator.Contains)]
        [InlineData(BoardFilterOperator.DoesNotContain)]
        [InlineData(BoardFilterOperator.Is)]
        [InlineData(BoardFilterOperator.IsNot)]
        [InlineData(BoardFilterOperator.IsEmpty)]
        [InlineData(BoardFilterOperator.IsNotEmpty)]
        public void BoardFilterOperator_AllValues_SerializeAndDeserializeAsString(BoardFilterOperator op)
        {
            var json = JsonSerializer.Serialize(op);
            Assert.Equal($"\"{op}\"", json);

            var deserialized = JsonSerializer.Deserialize<BoardFilterOperator>(json);
            Assert.Equal(op, deserialized);
        }
    }
}
