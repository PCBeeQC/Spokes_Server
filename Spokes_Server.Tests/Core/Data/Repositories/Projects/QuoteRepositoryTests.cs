using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class QuoteRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly SequenceService _sequenceService;
    private readonly QuoteRepository _repository;
    private readonly Mock<ILogger<QuoteRepository>> _mockLogger;

    public QuoteRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_QuoteRepo_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _sequenceService = new SequenceService(_config);
        _mockLogger = new Mock<ILogger<QuoteRepository>>();

        _repository = new QuoteRepository(
            _persistence,
            _config,
            _sequenceService,
            _mockLogger.Object
        );
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
    public void GetByProject_ReturnsQuotesForProject_OrderedByDateDescending()
    {
        // Arrange
        var projectId = "PROJ-01";
        var quote1 = new Quote { Id = "1", ProjectId = projectId, Date = new DateTime(2023, 1, 1) };
        var quote2 = new Quote { Id = "2", ProjectId = projectId, Date = new DateTime(2023, 1, 10) };
        var quote3 = new Quote { Id = "3", ProjectId = projectId, Date = new DateTime(2023, 1, 5) };
        var otherProjectQuote = new Quote { Id = "4", ProjectId = "OTHER", Date = new DateTime(2023, 1, 15) };

        _repository.Clear();
        _repository.Save(quote1);
        _repository.Save(quote2);
        _repository.Save(quote3);
        _repository.Save(otherProjectQuote);

        // Act
        var results = _repository.GetByProject(projectId);

        // Assert
        Assert.Equal(3, results.Count);
        // Verify descending order
        Assert.Equal("2", results[0].Id);
        Assert.Equal("3", results[1].Id);
        Assert.Equal("1", results[2].Id);
    }

    [Fact]
    public void GetByProject_WhenNoQuotes_ReturnsEmptyList()
    {
        // Arrange
        _repository.Clear();

        // Act
        var results = _repository.GetByProject("PROJ-99");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void GetByProject_NullProjectId_ReturnsEmptyList()
    {
        // Arrange
        _repository.Clear();
        _repository.Save(new Quote { Id = "1", ProjectId = "PROJ-01" });

        // Act
        var results = _repository.GetByProject(null);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void GenerateQuoteNumber_ValidPrefix_ReturnsExpectedFormat()
    {
        // Act
        var quoteNumber1 = _repository.GenerateQuoteNumber("QTE");
        var quoteNumber2 = _repository.GenerateQuoteNumber("QTE");

        // Assert
        Assert.NotNull(quoteNumber1);
        Assert.StartsWith("QTE-", quoteNumber1);
        Assert.EndsWith("-0001", quoteNumber1);

        Assert.NotNull(quoteNumber2);
        Assert.StartsWith("QTE-", quoteNumber2);
        Assert.EndsWith("-0002", quoteNumber2);
    }

    [Fact]
    public void LoadFromDisk_ValidJsonFiles_LoadsIntoCache()
    {
        // Arrange
        _repository.Clear();
        var projectId = "PROJ-100";
        var projectDir = Path.Combine(_testDataDir, "Projects", projectId, "Quotes");
        Directory.CreateDirectory(projectDir);

        var quote = new Quote { Id = "123", ProjectId = projectId, Title = "Test Load" };
        File.WriteAllText(Path.Combine(projectDir, "123.json"), JsonSerializer.Serialize(quote));

        // Act
        _repository.LoadFromDisk();

        // Assert
        var loadedQuote = _repository.GetById("123");
        Assert.NotNull(loadedQuote);
        Assert.Equal("Test Load", loadedQuote.Title);
    }

    [Fact]
    public void LoadFromDisk_InvalidJson_IgnoresAndLogsError()
    {
        // Arrange
        _repository.Clear();
        var projectDir = Path.Combine(_testDataDir, "Projects", "PROJ-ERR", "Quotes");
        Directory.CreateDirectory(projectDir);

        File.WriteAllText(Path.Combine(projectDir, "bad.json"), "{ invalid json }");

        // Act
        _repository.LoadFromDisk();

        // Assert
        var all = _repository.GetAll();
        Assert.Empty(all);
        
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
            Times.Once);
    }

    [Fact]
    public void LoadFromDisk_NoProjectsFolder_ReturnsSafely()
    {
        // Arrange
        _repository.Clear();
        var projectsPath = Path.Combine(_testDataDir, "Projects");
        if (Directory.Exists(projectsPath))
        {
            Directory.Delete(projectsPath, true);
        }

        // Act
        var ex = Record.Exception(() => _repository.LoadFromDisk());

        // Assert
        Assert.Null(ex); // Should not throw
        Assert.Empty(_repository.GetAll());
    }

    [Fact]
    public void Save_StoresQuoteInCorrectProjectDirectory()
    {
        // Arrange
        var projectId = "PROJ-500";
        var quoteId = "QTE-001";
        var quote = new Quote { Id = quoteId, ProjectId = projectId, Title = "Save Test" };

        // Act
        _repository.Save(quote);
        
        // Wait briefly for DiskPersistenceService to flush
        _persistence.FlushAll();

        // Assert
        var expectedPath = Path.Combine(_testDataDir, "Projects", projectId, "Quotes", $"{quoteId}.json");
        Assert.True(File.Exists(expectedPath), $"File should exist at {expectedPath}");
        
        var json = File.ReadAllText(expectedPath);
        Assert.Contains("Save Test", json);
    }
}
