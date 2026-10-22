namespace Spokes_Server.Tests.Aggregate.Contexts;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Xunit;

public class AccountingContextTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly AccountingContext _context;
    private readonly CompanyProfileRepository _companyProfileRepo;

    public AccountingContextTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _context = _serviceProvider.GetRequiredService<AccountingContext>();
        _companyProfileRepo = _serviceProvider.GetRequiredService<CompanyProfileRepository>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Resolution_FromServiceProvider_ResolvesAndPopulatesAllRepositories()
    {
        Assert.NotNull(_context.Invoices);
        Assert.NotNull(_context.PurchaseOrders);
        Assert.NotNull(_context.Suppliers);
        Assert.NotNull(_context.CommissionLedgerRecords);
        Assert.NotNull(_context.ExpenseReports);
        Assert.NotNull(_context.Bills);
        Assert.NotNull(_context.EmployerContributions);
        Assert.NotNull(_context.RecurringExpenses);
    }

    [Fact]
    public void Config_WhenProfilePresent_DelegatesToCompanyProfile()
    {
        var profile = _companyProfileRepo.GetOrCreate();
        profile.Accounting.CurrencySymbol = "EUR";
        _companyProfileRepo.Save(profile);

        var config = _context.GetAccountingConfig();
        Assert.NotNull(config);
        Assert.Equal("EUR", config.CurrencySymbol);
        Assert.Equal("EUR", _context.Config.CurrencySymbol);
    }

    [Fact]
    public void Initialize_ExecutesWithoutExceptions()
    {
        var exception = Record.Exception(() => _context.Initialize());
        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WhenCompanyProfileNull_ReturnsDefaultConfig()
    {
        var manualContext = new AccountingContext(
            _context.Invoices,
            _context.PurchaseOrders,
            _context.Suppliers,
            _context.CommissionLedgerRecords,
            _context.ExpenseReports,
            _context.Bills,
            _context.EmployerContributions,
            _context.RecurringExpenses,
            companyProfile: null);

        var config = manualContext.GetAccountingConfig();
        Assert.NotNull(config);
        Assert.NotNull(manualContext.Config);
    }
}
