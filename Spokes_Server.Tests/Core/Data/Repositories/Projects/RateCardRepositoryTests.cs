using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class RateCardRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly RateCardRepository _repository;

    public RateCardRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_RateCard_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        
        _repository = new RateCardRepository(_persistence, _config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    [Fact]
    public void Save_CreatesFileWithRateCardId()
    {
        // Arrange
        var rateCard = new RateCard { Name = "Test Rate Card" };

        // Act
        _repository.Save(rateCard);
        _persistence.FlushAll();

        // Assert
        var expectedPath = Path.Combine(_testDataDir, "Settings", "RateCards", $"{rateCard.Id}.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetById_RetrievesSavedRateCard()
    {
        // Arrange
        var rateCard = new RateCard { Name = "Test Rate Card", MarkupRate = 0.20m };
        _repository.Save(rateCard);

        // Act
        var retrieved = _repository.GetById(rateCard.Id);

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
        _repository.Save(rateCard);
        Assert.NotNull(_repository.GetById(rateCard.Id));

        // Act
        _repository.Delete(rateCard.Id);

        // Assert
        Assert.Null(_repository.GetById(rateCard.Id));
    }
    
    [Fact]
    public void GetAll_ReturnsAllSavedRateCards()
    {
        // Arrange
        var rateCard1 = new RateCard { Name = "Card 1" };
        var rateCard2 = new RateCard { Name = "Card 2" };
        _repository.Save(rateCard1);
        _repository.Save(rateCard2);
        
        // Act
        var all = _repository.GetAll();
        
        // Assert
        Assert.Contains(all, rc => rc.Id == rateCard1.Id);
        Assert.Contains(all, rc => rc.Id == rateCard2.Id);
    }
}
