namespace Spokes_Server.Tests.Aggregate.Contexts;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Core;
using Xunit;

public class ProjectsContextTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly ProjectsContext _context;
    private readonly CompanyProfileRepository _companyProfileRepo;

    public ProjectsContextTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _context = _serviceProvider.GetRequiredService<ProjectsContext>();
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
        Assert.NotNull(_context.Projects);
        Assert.NotNull(_context.ProjectGroups);
        Assert.NotNull(_context.WorkTypes);
        Assert.NotNull(_context.RateCards);
        Assert.NotNull(_context.Quotes);
        Assert.NotNull(_context.ProjectNotes);
        Assert.NotNull(_context.Boards);
        Assert.NotNull(_context.BoardCards);
        Assert.NotNull(_context.BoardTemplates);
        Assert.NotNull(_context.ProjectDocuments);
    }

    [Fact]
    public void Config_WhenProfilePresent_DelegatesToCompanyProfile()
    {
        var profile = _companyProfileRepo.GetOrCreate();
        profile.DocumentNumbering.InvoicePrefix = "INV-TEST";
        _companyProfileRepo.Save(profile);

        Assert.Equal("INV-TEST", _context.GetDocumentNumbering().InvoicePrefix);
        Assert.Equal("INV-TEST", _context.DocumentNumbering.InvoicePrefix);
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
        var manualContext = new ProjectsContext(
            _context.Projects,
            _context.ProjectGroups,
            _context.WorkTypes,
            _context.RateCards,
            _context.Quotes,
            _context.ProjectNotes,
            _context.Boards,
            _context.BoardCards,
            _context.BoardTemplates,
            _context.ProjectDocuments,
            companyProfile: null);

        Assert.NotNull(manualContext.GetDocumentNumbering());
        Assert.NotNull(manualContext.DocumentNumbering);
    }
}
