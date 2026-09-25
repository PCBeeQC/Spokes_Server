using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class FinancialCalculationEngineTests
{
    private static FinancialCalculationEngine CreateEngine()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        var sequence = new SequenceService(config);
        var invoices = new InvoiceRepository(persistence, config, sequence);
        var quotes = new QuoteRepository(persistence, config, sequence, Mock.Of<ILogger<QuoteRepository>>());
        var projects = new ProjectRepository(persistence, config);

        return new FinancialCalculationEngine(invoices, quotes, projects);
    }

    [Fact]
    public void Constructor_CanInstantiateWithMockedRepositories()
    {
        var mockConfig = new Mock<IConfiguration>();
        var mockPersistence = new Mock<DiskPersistenceService>(Mock.Of<ILogger<DiskPersistenceService>>());
        var mockSequence = new Mock<SequenceService>(mockConfig.Object);
        var mockInvoices = new Mock<InvoiceRepository>(mockPersistence.Object, mockConfig.Object, mockSequence.Object);
        var mockQuotes = new Mock<QuoteRepository>(mockPersistence.Object, mockConfig.Object, mockSequence.Object, Mock.Of<ILogger<QuoteRepository>>());
        var mockProjects = new Mock<ProjectRepository>(mockPersistence.Object, mockConfig.Object);

        var engine = new FinancialCalculationEngine(mockInvoices.Object, mockQuotes.Object, mockProjects.Object);

        Assert.NotNull(engine);
    }

    [Fact]
    public void Constructor_CanInstantiateWithDummyRepositories()
    {
        var engine = CreateEngine();

        Assert.NotNull(engine);
    }

    [Theory]
    [InlineData(100.00, 0.15, 15.00)]
    [InlineData(250.50, 0.05, 12.53)]
    [InlineData(50.00, 0.09975, 4.99)]
    public void CalculateTax_PositiveSubtotalAndTaxRate_CalculatesExpectedTax(decimal subtotal, decimal taxRate, decimal expected)
    {
        var engine = CreateEngine();

        var result = engine.CalculateTax(subtotal, taxRate);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(10.055, 1.0, 10.06)]
    [InlineData(10.025, 1.0, 10.03)]
    [InlineData(100.00, 0.05555, 5.56)]
    [InlineData(-10.055, 1.0, -10.06)]
    [InlineData(-10.025, 1.0, -10.03)]
    public void CalculateTax_MidpointRounding_RoundsAwayFromZero(decimal subtotal, decimal taxRate, decimal expected)
    {
        var engine = CreateEngine();

        var result = engine.CalculateTax(subtotal, taxRate);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculateTax_ZeroSubtotal_ReturnsZero()
    {
        var engine = CreateEngine();

        var result = engine.CalculateTax(0m, 0.15m);

        Assert.Equal(0.00m, result);
    }

    [Fact]
    public void CalculateTax_ZeroTaxRate_ReturnsZero()
    {
        var engine = CreateEngine();

        var result = engine.CalculateTax(100m, 0m);

        Assert.Equal(0.00m, result);
    }

    [Theory]
    [InlineData(-100.00, 0.15, -15.00)]
    [InlineData(-50.00, 0.05, -2.50)]
    public void CalculateTax_NegativeSubtotal_ReturnsNegativeTax(decimal subtotal, decimal taxRate, decimal expected)
    {
        var engine = CreateEngine();

        var result = engine.CalculateTax(subtotal, taxRate);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-50, 40)]
    [InlineData(-100, 0)]
    [InlineData(-0.01, 10)]
    public void CalculateMarginPercentage_SubtotalLessThanOrEqualToZero_ReturnsZero(decimal subtotal, decimal totalCost)
    {
        var engine = CreateEngine();

        var result = engine.CalculateMarginPercentage(subtotal, totalCost);

        Assert.Equal(0m, result);
    }

    [Theory]
    [InlineData(100.00, 40.00, 60.00)]
    [InlineData(200.00, 50.00, 75.00)]
    [InlineData(80.00, 20.00, 75.00)]
    public void CalculateMarginPercentage_StandardCalculation_ReturnsExpectedPercentage(decimal subtotal, decimal totalCost, decimal expected)
    {
        var engine = CreateEngine();

        var result = engine.CalculateMarginPercentage(subtotal, totalCost);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculateMarginPercentage_TotalCostEqualsSubtotal_ReturnsZero()
    {
        var engine = CreateEngine();

        var result = engine.CalculateMarginPercentage(100m, 100m);

        Assert.Equal(0.00m, result);
    }

    [Fact]
    public void CalculateMarginPercentage_TotalCostIsZero_ReturnsOneHundred()
    {
        var engine = CreateEngine();

        var result = engine.CalculateMarginPercentage(100m, 0m);

        Assert.Equal(100.00m, result);
    }

    [Theory]
    [InlineData(100.00, 150.00, -50.00)]
    [InlineData(200.00, 300.00, -50.00)]
    [InlineData(50.00, 100.00, -100.00)]
    public void CalculateMarginPercentage_CostExceedsSubtotal_ReturnsNegativeMargin(decimal subtotal, decimal totalCost, decimal expected)
    {
        var engine = CreateEngine();

        var result = engine.CalculateMarginPercentage(subtotal, totalCost);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(200.00, 66.89, 66.56)]     // (133.11 / 200) * 100 = 66.555 -> 66.56
    [InlineData(1000.00, 874.55, 12.55)]   // (125.45 / 1000) * 100 = 12.545 -> 12.55
    [InlineData(200.00, 333.11, -66.56)]   // (-133.11 / 200) * 100 = -66.555 -> -66.56
    public void CalculateMarginPercentage_MidpointRounding_RoundsAwayFromZero(decimal subtotal, decimal totalCost, decimal expected)
    {
        var engine = CreateEngine();

        var result = engine.CalculateMarginPercentage(subtotal, totalCost);

        Assert.Equal(expected, result);
    }
}
