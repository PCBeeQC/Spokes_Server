using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Tests.Core.Services.UI;

public class AppLabelServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly AppLabelService _service;

    public AppLabelServiceTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _service = new AppLabelService(_db.CoreContext);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    private void SetEdition(string? edition)
    {
        var profile = _db.CompanyProfile.Get();
        profile.Edition = edition!;
        _db.CompanyProfile.Save(profile);
    }

    [Fact]
    public void AppLabels_WhenEditionIsBusiness_ReturnsBusinessLabels()
    {
        SetEdition("Business");

        Assert.Equal("Team", _service.Team);
        Assert.Equal("Teams", _service.Teams);
        Assert.Equal("Employee", _service.Employee);
        Assert.Equal("Employees", _service.Employees);
        Assert.Equal("Favorites", _service.Favorites);
        Assert.Equal("Company", _service.Company);
        Assert.Equal("Business Email", _service.Email);
    }

    [Fact]
    public void AppLabels_WhenEditionIsFamily_ReturnsFamilyLabels()
    {
        SetEdition("Family");

        Assert.Equal("Group", _service.Team);
        Assert.Equal("Groups", _service.Teams);
        Assert.Equal("Member", _service.Employee);
        Assert.Equal("Members", _service.Employees);
        Assert.Equal("Menu", _service.Favorites);
        Assert.Equal("Server", _service.Company);
        Assert.Equal("Email", _service.Email);
    }

    [Theory]
    [InlineData("Community")]
    [InlineData("Enterprise")]
    [InlineData("Pro")]
    [InlineData("Unknown")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AppLabels_WhenEditionIsOther_FallsBackToBusinessLabels(string? edition)
    {
        SetEdition(edition);

        Assert.Equal("Team", _service.Team);
        Assert.Equal("Teams", _service.Teams);
        Assert.Equal("Employee", _service.Employee);
        Assert.Equal("Employees", _service.Employees);
        Assert.Equal("Favorites", _service.Favorites);
        Assert.Equal("Company", _service.Company);
        Assert.Equal("Business Email", _service.Email);
    }

    [Fact]
    public void AppLabels_DynamicallyReflectsEditionChanges()
    {
        SetEdition("Business");
        Assert.Equal("Team", _service.Team);
        Assert.Equal("Employee", _service.Employee);

        SetEdition("Family");
        Assert.Equal("Group", _service.Team);
        Assert.Equal("Member", _service.Employee);

        SetEdition("Community");
        Assert.Equal("Team", _service.Team);
        Assert.Equal("Employee", _service.Employee);
    }
}
