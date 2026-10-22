using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Accounting;

public class InvoiceRepositoryTests : RepositoryTestFixture<InvoiceRepository>
{
    public InvoiceRepositoryTests()
        : base((writer, config) => new InvoiceRepository(writer, config, new SequenceService(config)), "Invoices")
    {
    }

    [Fact]
    public void GenerateInvoiceNumber_DelegatesToSequenceService()
    {
        var num1 = _repo.GenerateInvoiceNumber("INV");
        var num2 = _repo.GenerateInvoiceNumber("INV");

        Assert.StartsWith("INV", num1);
        Assert.StartsWith("INV", num2);
        Assert.NotEqual(num1, num2);
    }

    [Fact]
    public void GetByProject_ReturnsMatchingOrderedByDateDescending()
    {
        var i1 = new Invoice { Id = "i1", ProjectId = "p1", Date = DateTime.UtcNow.AddDays(-2) };
        var i2 = new Invoice { Id = "i2", ProjectId = "p1", Date = DateTime.UtcNow };
        var i3 = new Invoice { Id = "i3", ProjectId = "p2", Date = DateTime.UtcNow.AddDays(-1) };

        _repo.Save(i1);
        _repo.Save(i2);
        _repo.Save(i3);

        var result = _repo.GetByProject("p1");

        Assert.Equal(2, result.Count);
        Assert.Equal("i2", result[0].Id);
        Assert.Equal("i1", result[1].Id);
    }

    [Fact]
    public void GetByProjectGroup_ReturnsMatchingOrderedByDateDescending()
    {
        var i1 = new Invoice { Id = "i1", ProjectGroupId = "pg1", Date = DateTime.UtcNow.AddDays(-4) };
        var i2 = new Invoice { Id = "i2", ProjectGroupId = "pg1", Date = DateTime.UtcNow.AddDays(-1) };
        var i3 = new Invoice { Id = "i3", ProjectGroupId = "pg2", Date = DateTime.UtcNow };

        _repo.Save(i1);
        _repo.Save(i2);
        _repo.Save(i3);

        var result = _repo.GetByProjectGroup("pg1");

        Assert.Equal(2, result.Count);
        Assert.Equal("i2", result[0].Id);
        Assert.Equal("i1", result[1].Id);
    }

    [Fact]
    public void GetFilePath_WritesDirectJsonFileInInvoicesFolder()
    {
        var invoice = new Invoice { Id = "inv-100" };

        _repo.Save(invoice);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Invoices", "inv-100.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetByProject_WhenNoMatches_ReturnsEmptyList()
    {
        var invoice = new Invoice { Id = "inv-1", ProjectId = "project-1" };
        _repo.Save(invoice);

        var result = _repo.GetByProject("unknown");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetByProjectGroup_WhenNoMatches_ReturnsEmptyList()
    {
        var invoice = new Invoice { Id = "inv-1", ProjectGroupId = "pg-1" };
        _repo.Save(invoice);

        var result = _repo.GetByProjectGroup("unknown");

        Assert.NotNull(result);
        Assert.Empty(result);
    }
}
