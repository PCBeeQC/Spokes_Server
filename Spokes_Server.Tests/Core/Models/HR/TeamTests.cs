using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models.HR;

public class TeamTests
{
    [Fact]
    public void Initialization_SetsExpectedDefaults()
    {
        // Act
        var team = new Team();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(team.Id));
        Assert.True(Guid.TryParse(team.Id, out var parsedGuid));
        Assert.NotEqual(Guid.Empty, parsedGuid);
        Assert.Equal(string.Empty, team.Name);
        Assert.Equal(string.Empty, team.LeaderId);
        Assert.Equal(string.Empty, team.Description);
        Assert.Equal("#1E88E5", team.Color);
    }

    [Fact]
    public void PropertyMutations_UpdatesAndReadsProperties()
    {
        // Arrange
        var team = new Team();

        // Act
        team.Id = "custom-team-id";
        team.Name = "Backend Engineering";
        team.LeaderId = "emp-007";
        team.Description = "Core platform development and maintenance";
        team.Color = "#4CAF50";

        // Assert
        Assert.Equal("custom-team-id", team.Id);
        Assert.Equal("Backend Engineering", team.Name);
        Assert.Equal("emp-007", team.LeaderId);
        Assert.Equal("Core platform development and maintenance", team.Description);
        Assert.Equal("#4CAF50", team.Color);
    }

    [Fact]
    public void Equals_SameReference_ReturnsTrue()
    {
        // Arrange
        var team = new Team();

        // Act & Assert
        Assert.True(team.Equals(team));
    }

    [Fact]
    public void Equals_SameId_ReturnsTrue()
    {
        // Arrange
        var sharedId = Guid.NewGuid().ToString();
        var team1 = new Team { Id = sharedId, Name = "Team Alpha" };
        var team2 = new Team { Id = sharedId, Name = "Team Beta" };

        // Act & Assert
        Assert.True(team1.Equals(team2));
        Assert.True(team2.Equals(team1));
    }

    [Fact]
    public void Equals_DifferentId_ReturnsFalse()
    {
        // Arrange
        var team1 = new Team { Id = "team-1" };
        var team2 = new Team { Id = "team-2" };

        // Act & Assert
        Assert.False(team1.Equals(team2));
        Assert.False(team2.Equals(team1));
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        // Arrange
        var team = new Team();

        // Act & Assert
        Assert.False(team.Equals(null));
    }

    [Fact]
    public void Equals_DifferentType_ReturnsFalse()
    {
        // Arrange
        var team = new Team();
        var other = new object();

        // Act & Assert
        Assert.False(team.Equals(other));
        Assert.False(team.Equals("team-string"));
    }

    [Fact]
    public void GetHashCode_SameId_ReturnsMatchingHashCode()
    {
        // Arrange
        var id = "consistent-team-id-99";
        var team1 = new Team { Id = id };
        var team2 = new Team { Id = id };

        // Act & Assert
        Assert.Equal(team1.GetHashCode(), team2.GetHashCode());
        Assert.Equal(id.GetHashCode(), team1.GetHashCode());
    }

    [Fact]
    public void GetHashCode_NullId_FallsBackWithoutThrowingException()
    {
        // Arrange
        var team = new Team { Id = null! };

        // Act
        var ex = Record.Exception(() => team.GetHashCode());

        // Assert
        Assert.Null(ex);
        Assert.IsType<int>(team.GetHashCode());
    }
}
