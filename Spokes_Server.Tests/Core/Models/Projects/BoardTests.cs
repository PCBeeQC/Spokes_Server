using Spokes_Server.Core.Models.Projects;
using System.Text.Json;

namespace Spokes_Server.Tests.Core.Models.Projects;

public class BoardTests
{
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
