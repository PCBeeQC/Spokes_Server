using System.Text.Json;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class RecurringExpenseRepositoryTests : RepositoryTestFixture<RecurringExpenseRepository>
{
    [Fact]
    public void Save_AddsToCache()
    {
        var item = new RecurringExpense { Id = "re1", Name = "Office Rent", Amount = 1500 };

        _repo.Save(item);

        var storedItems = _repo.GetAll();
        Assert.Single(storedItems);
        Assert.Equal("re1", storedItems.First().Id);
        Assert.Equal("Office Rent", storedItems.First().Name);
    }

    [Fact]
    public void LoadFromDisk_ReadsFromExpectedPath()
    {
        var expectedDir = Path.Combine(_testDataDir, "RecurringExpenses", "re2");
        Directory.CreateDirectory(expectedDir);

        var item = new RecurringExpense { Id = "re2", Name = "Internet" };
        File.WriteAllText(Path.Combine(expectedDir, "expense.json"), JsonSerializer.Serialize(item));

        _repo.LoadFromDisk();

        var storedItems = _repo.GetAll();
        Assert.Contains(storedItems, i => i.Id == "re2" && i.Name == "Internet");
    }

    [Fact]
    public void Delete_RemovesFromCache()
    {
        var item = new RecurringExpense { Id = "re3", Name = "Cleaning" };

        _repo.Save(item);
        Assert.Single(_repo.GetAll());

        _repo.Delete("re3");
        Assert.Empty(_repo.GetAll());
    }

    [Fact]
    public void GetFilePath_SavesRecurringExpenseInSubfolderWithExpenseJson()
    {
        var item = new RecurringExpense { Id = "re-subfolder", Name = "Software Subscription", Amount = 50 };

        _repo.Save(item);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "RecurringExpenses", "re-subfolder", "expense.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetById_ReturnsMatchingExpense()
    {
        var item = new RecurringExpense { Id = "re-find", Name = "Cloud Hosting", Amount = 120 };
        _repo.Save(item);

        var result = _repo.GetById("re-find");

        Assert.NotNull(result);
        Assert.Equal(120, result.Amount);
    }

    [Fact]
    public void Delete_RemovesFromDiskWhenFlushed()
    {
        var item = new RecurringExpense { Id = "re-disk-del", Name = "Water Cooler" };
        _repo.Save(item);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "RecurringExpenses", "re-disk-del", "expense.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("re-disk-del");
        _writer.FlushAll();

        Assert.False(File.Exists(expectedPath));
    }
}
