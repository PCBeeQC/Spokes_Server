using System.Text.Json;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class ExpenseReportRepositoryTests : RepositoryTestFixture<ExpenseReportRepository>
{
    public ExpenseReportRepositoryTests()
        : base((writer, config) => new ExpenseReportRepository(writer, config, new SequenceService(config)), "Expenses")
    {
    }

    [Fact]
    public void Save_GeneratesReportNumberIfMissing()
    {
        var item = new ExpenseReport { Id = "exp1", ReportNumber = null };

        _repo.Save(item);

        var storedItems = _repo.GetAll();
        Assert.Single(storedItems);
        Assert.NotNull(storedItems.First().ReportNumber);
        Assert.StartsWith("EXP", storedItems.First().ReportNumber);
    }

    [Fact]
    public void Save_DoesNotOverwriteExistingReportNumber()
    {
        var item = new ExpenseReport { Id = "exp2", ReportNumber = "CUSTOM-123" };

        _repo.Save(item);

        var storedItems = _repo.GetAll();
        Assert.Single(storedItems);
        Assert.Equal("CUSTOM-123", storedItems.First().ReportNumber);
    }

    [Fact]
    public void LoadFromDisk_ReadsFromExpectedPath()
    {
        var expectedDir = Path.Combine(_testDataDir, "Expenses");
        Directory.CreateDirectory(expectedDir);

        var item = new ExpenseReport { Id = "exp3", ReportNumber = "EXP-999" };
        File.WriteAllText(Path.Combine(expectedDir, "exp3.json"), JsonSerializer.Serialize(item));

        _repo.LoadFromDisk();

        var storedItems = _repo.GetAll();
        Assert.Contains(storedItems, i => i.Id == "exp3" && i.ReportNumber == "EXP-999");
    }

    [Fact]
    public void GetFilePath_WritesDirectJsonFileInExpensesFolder()
    {
        var item = new ExpenseReport { Id = "exp-100" };

        _repo.Save(item);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Expenses", "exp-100.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Save_WhenReportNumberIsEmptyString_GeneratesNewNumber()
    {
        var item = new ExpenseReport { Id = "exp-empty", ReportNumber = string.Empty };

        _repo.Save(item);

        var storedItems = _repo.GetAll();
        var savedItem = storedItems.FirstOrDefault(i => i.Id == "exp-empty");
        Assert.NotNull(savedItem);
        Assert.False(string.IsNullOrEmpty(savedItem.ReportNumber));
        Assert.StartsWith("EXP", savedItem.ReportNumber);
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var item = new ExpenseReport { Id = "exp-del" };

        _repo.Save(item);
        _writer.FlushAll();
        var expectedPath = Path.Combine(_testDataDir, "Expenses", "exp-del.json");
        Assert.True(File.Exists(expectedPath));
        Assert.Contains(_repo.GetAll(), r => r.Id == "exp-del");

        _repo.Delete("exp-del");
        _writer.FlushAll();

        Assert.DoesNotContain(_repo.GetAll(), r => r.Id == "exp-del");
        Assert.False(File.Exists(expectedPath));
    }
}
