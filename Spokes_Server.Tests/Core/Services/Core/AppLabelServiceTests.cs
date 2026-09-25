using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

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
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _service = new AppLabelService(_db);
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
    public void AppLabels_WhenEditionIsOther_FallsBackToBusinessLabels(string edition)
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
    public void AppLabels_WhenEditionIsNull_FallsBackToBusinessLabels()
    {
        SetEdition(null);

        Assert.Equal("Team", _service.Team);
        Assert.Equal("Teams", _service.Teams);
        Assert.Equal("Employee", _service.Employee);
        Assert.Equal("Employees", _service.Employees);
        Assert.Equal("Favorites", _service.Favorites);
        Assert.Equal("Company", _service.Company);
        Assert.Equal("Business Email", _service.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AppLabels_WhenEditionIsEmptyOrWhitespace_FallsBackToBusinessLabels(string edition)
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

    [Theory]
    [InlineData("Business", "Team")]
    [InlineData("Family", "Group")]
    [InlineData("Community", "Team")]
    [InlineData(null, "Team")]
    public void Team_ReturnsExpectedLabel_ForEachEdition(string? edition, string expected)
    {
        SetEdition(edition);
        Assert.Equal(expected, _service.Team);
    }

    [Theory]
    [InlineData("Business", "Teams")]
    [InlineData("Family", "Groups")]
    [InlineData("Community", "Teams")]
    [InlineData(null, "Teams")]
    public void Teams_ReturnsExpectedLabel_ForEachEdition(string? edition, string expected)
    {
        SetEdition(edition);
        Assert.Equal(expected, _service.Teams);
    }

    [Theory]
    [InlineData("Business", "Employee")]
    [InlineData("Family", "Member")]
    [InlineData("Community", "Employee")]
    [InlineData(null, "Employee")]
    public void Employee_ReturnsExpectedLabel_ForEachEdition(string? edition, string expected)
    {
        SetEdition(edition);
        Assert.Equal(expected, _service.Employee);
    }

    [Theory]
    [InlineData("Business", "Employees")]
    [InlineData("Family", "Members")]
    [InlineData("Community", "Employees")]
    [InlineData(null, "Employees")]
    public void Employees_ReturnsExpectedLabel_ForEachEdition(string? edition, string expected)
    {
        SetEdition(edition);
        Assert.Equal(expected, _service.Employees);
    }

    [Theory]
    [InlineData("Business", "Favorites")]
    [InlineData("Family", "Menu")]
    [InlineData("Community", "Favorites")]
    [InlineData(null, "Favorites")]
    public void Favorites_ReturnsExpectedLabel_ForEachEdition(string? edition, string expected)
    {
        SetEdition(edition);
        Assert.Equal(expected, _service.Favorites);
    }

    [Theory]
    [InlineData("Business", "Company")]
    [InlineData("Family", "Server")]
    [InlineData("Community", "Company")]
    [InlineData(null, "Company")]
    public void Company_ReturnsExpectedLabel_ForEachEdition(string? edition, string expected)
    {
        SetEdition(edition);
        Assert.Equal(expected, _service.Company);
    }

    [Theory]
    [InlineData("Business", "Business Email")]
    [InlineData("Family", "Email")]
    [InlineData("Community", "Business Email")]
    [InlineData(null, "Business Email")]
    public void Email_ReturnsExpectedLabel_ForEachEdition(string? edition, string expected)
    {
        SetEdition(edition);
        Assert.Equal(expected, _service.Email);
    }
}
