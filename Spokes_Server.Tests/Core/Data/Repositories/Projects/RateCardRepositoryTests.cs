using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class RateCardRepositoryTests : RepositoryTestFixture<RateCardRepository>
{
    [Fact]
    public void Save_CreatesFileWithRateCardId()
    {
        // Arrange
        var rateCard = new RateCard { Name = "Test Rate Card" };

        // Act
        _repo.Save(rateCard);
        _writer.FlushAll();

        // Assert
        var expectedPath = Path.Combine(_testDataDir, "Settings", "RateCards", $"{rateCard.Id}.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetById_RetrievesSavedRateCard()
    {
        // Arrange
        var rateCard = new RateCard { Name = "Test Rate Card", MarkupRate = 0.20m };
        _repo.Save(rateCard);

        // Act
        var retrieved = _repo.GetById(rateCard.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Test Rate Card", retrieved.Name);
        Assert.Equal(0.20m, retrieved.MarkupRate);
    }

    [Fact]
    public void Delete_RemovesSavedRateCard()
    {
        // Arrange
        var rateCard = new RateCard { Name = "Test Rate Card" };
        _repo.Save(rateCard);
        Assert.NotNull(_repo.GetById(rateCard.Id));

        // Act
        _repo.Delete(rateCard.Id);

        // Assert
        Assert.Null(_repo.GetById(rateCard.Id));
    }
    
    [Fact]
    public void GetAll_ReturnsAllSavedRateCards()
    {
        // Arrange
        var rateCard1 = new RateCard { Name = "Card 1" };
        var rateCard2 = new RateCard { Name = "Card 2" };
        _repo.Save(rateCard1);
        _repo.Save(rateCard2);
        
        // Act
        var all = _repo.GetAll();
        
        // Assert
        Assert.Contains(all, rc => rc.Id == rateCard1.Id);
        Assert.Contains(all, rc => rc.Id == rateCard2.Id);
    }
}
