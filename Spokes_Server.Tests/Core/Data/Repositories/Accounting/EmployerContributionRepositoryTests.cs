using System.Text.Json;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class EmployerContributionRepositoryTests : RepositoryTestFixture<EmployerContributionRepository>
{
    [Fact]
    public void Save_AddsToCache()
    {
        var item = new EmployerContribution { Id = "ec1", Name = "Health Insurance" };

        _repo.Save(item);

        var storedItems = _repo.GetAll();
        Assert.Single(storedItems);
        Assert.Equal("ec1", storedItems.First().Id);
        Assert.Equal("Health Insurance", storedItems.First().Name);
    }

    [Fact]
    public void LoadFromDisk_ReadsFromExpectedPath()
    {
        var expectedDir = Path.Combine(_testDataDir, "EmployerContributions", "ec3");
        Directory.CreateDirectory(expectedDir);

        var item = new EmployerContribution { Id = "ec3", Name = "Pension" };
        File.WriteAllText(Path.Combine(expectedDir, "contribution.json"), JsonSerializer.Serialize(item));

        _repo.LoadFromDisk();

        var storedItems = _repo.GetAll();
        Assert.Contains(storedItems, i => i.Id == "ec3" && i.Name == "Pension");
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var item = new EmployerContribution { Id = "ec2", Name = "401k" };

        _repo.Save(item);
        Assert.Single(_repo.GetAll());

        _repo.Delete("ec2");
        Assert.Empty(_repo.GetAll());
    }

    [Fact]
    public void GetFilePath_WritesToNestedFolderPerItem()
    {
        var item = new EmployerContribution { Id = "ec-100", Name = "Health Insurance" };

        _repo.Save(item);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "EmployerContributions", "ec-100", "contribution.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetById_ReturnsCorrectItem()
    {
        var item = new EmployerContribution { Id = "ec-100", Name = "Health Insurance" };
        _repo.Save(item);

        var retrieved = _repo.GetById("ec-100");
        Assert.NotNull(retrieved);
        Assert.Equal("ec-100", retrieved.Id);
        Assert.Equal("Health Insurance", retrieved.Name);

        var notFound = _repo.GetById("unknown-id");
        Assert.Null(notFound);
    }
}
