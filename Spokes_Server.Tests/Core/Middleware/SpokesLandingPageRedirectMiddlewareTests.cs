using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Tests.Helpers;

namespace Spokes_Server.Tests.Core.Middleware;

public class SpokesLandingPageRedirectMiddlewareTests : TestDataTestBase
{
    private readonly DiskPersistenceService _persistence;
    private readonly CompanyProfileRepository _companyProfileRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly OpenIdAccountRepository _openIdAccountRepo;
    private readonly UserService _userService;
    private readonly ServiceProvider _serviceProvider;

    public SpokesLandingPageRedirectMiddlewareTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(mockLogger.Object);
        _companyProfileRepo = new CompanyProfileRepository(_persistence, mockConfig.Object);
        _employeeRepo = new EmployeeRepository(_persistence, mockConfig.Object);
        _openIdAccountRepo = new OpenIdAccountRepository(_persistence, mockConfig.Object);

        var mockAuthStateProvider = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        _userService = new UserService(mockAuthStateProvider.Object, _employeeRepo, _openIdAccountRepo, _companyProfileRepo, null);

        var services = new ServiceCollection();
        services.AddSingleton(_companyProfileRepo);
        services.AddSingleton(_employeeRepo);
        services.AddSingleton(_openIdAccountRepo);
        services.AddSingleton(_userService);
        _serviceProvider = services.BuildServiceProvider();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        _persistence.Dispose();
        base.Dispose();
    }

    private RequestDelegate BuildPipeline()
    {
        var app = new ApplicationBuilder(_serviceProvider);
        app.UseSpokesLandingPageRedirect();
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        return app.Build();
    }

    [Fact]
    public async Task WhenUnauthenticated_BypassesRedirect()
    {
        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext { RequestServices = _serviceProvider };
        context.Request.Path = "/";
        context.Request.Method = "GET";

        await pipeline(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task WhenFamilyEdition_BypassesRedirect()
    {
        var profile = _companyProfileRepo.Get();
        profile.Edition = "Family";
        _companyProfileRepo.Save(profile);

        var emp = new Employee { Id = "emp-1", LandingPage = "email" };
        _employeeRepo.Save(emp);

        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext { RequestServices = _serviceProvider };
        context.Request.Path = "/";
        context.Request.Method = "GET";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("EmployeeId", "emp-1"),
            new Claim(ClaimTypes.NameIdentifier, "sub-1")
        ], "TestAuth"));

        await pipeline(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task WhenBusinessEdition_WithoutLandingPage_BypassesRedirect()
    {
        var profile = _companyProfileRepo.Get();
        profile.Edition = "Business";
        _companyProfileRepo.Save(profile);

        var emp = new Employee { Id = "emp-2", LandingPage = null };
        _employeeRepo.Save(emp);

        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext { RequestServices = _serviceProvider };
        context.Request.Path = "/";
        context.Request.Method = "GET";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("EmployeeId", "emp-2"),
            new Claim(ClaimTypes.NameIdentifier, "sub-2")
        ], "TestAuth"));

        await pipeline(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task WhenBusinessEdition_WithLandingPage_RedirectsToConfiguredPage()
    {
        var profile = _companyProfileRepo.Get();
        profile.Edition = "Business";
        _companyProfileRepo.Save(profile);

        var emp = new Employee { Id = "emp-3", LandingPage = "email" };
        _employeeRepo.Save(emp);

        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext { RequestServices = _serviceProvider };
        context.Request.Path = "/";
        context.Request.Method = "GET";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("EmployeeId", "emp-3"),
            new Claim(ClaimTypes.NameIdentifier, "sub-3")
        ], "TestAuth"));

        await pipeline(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/email", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task WhenPathIsNotRoot_BypassesRedirect()
    {
        var profile = _companyProfileRepo.Get();
        profile.Edition = "Business";
        _companyProfileRepo.Save(profile);

        var emp = new Employee { Id = "emp-4", LandingPage = "email" };
        _employeeRepo.Save(emp);

        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext { RequestServices = _serviceProvider };
        context.Request.Path = "/projects";
        context.Request.Method = "GET";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("EmployeeId", "emp-4"),
            new Claim(ClaimTypes.NameIdentifier, "sub-4")
        ], "TestAuth"));

        await pipeline(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }
}
