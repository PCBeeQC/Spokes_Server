namespace Spokes_Server.Tests.Aggregate.Contexts;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Xunit;

public class HrContextTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly HrContext _context;
    private readonly CompanyProfileRepository _companyProfileRepo;

    public HrContextTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _context = _serviceProvider.GetRequiredService<HrContext>();
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
        Assert.NotNull(_context.Employees);
        Assert.NotNull(_context.Timesheets);
        Assert.NotNull(_context.HourBankAdjustments);
        Assert.NotNull(_context.WeeklyPlans);
        Assert.NotNull(_context.Teams);
        Assert.NotNull(_context.CalendarEvents);
    }

    [Fact]
    public void Config_WhenProfilePresent_DelegatesToCompanyProfile()
    {
        var profile = _companyProfileRepo.GetOrCreate();
        profile.HRConfig.DailyOvertimeThreshold = 7.5m;
        profile.TimesheetConfig.LockTimesheetsOlderThanDays = 45;
        _companyProfileRepo.Save(profile);

        Assert.Equal(7.5m, _context.GetHRSettings().DailyOvertimeThreshold);
        Assert.Equal(7.5m, _context.HRConfig.DailyOvertimeThreshold);

        Assert.Equal(45, _context.GetTimesheetSettings().LockTimesheetsOlderThanDays);
        Assert.Equal(45, _context.TimesheetConfig.LockTimesheetsOlderThanDays);
    }

    [Fact]
    public void Initialize_ExecutesWithoutExceptions()
    {
        var exception = Record.Exception(() => _context.Initialize());
        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WhenCompanyProfileNull_ReturnsDefaultConfigs()
    {
        var manualContext = new HrContext(
            _context.Employees,
            _context.Timesheets,
            _context.HourBankAdjustments,
            _context.WeeklyPlans,
            _context.Teams,
            _context.CalendarEvents,
            companyProfile: null);

        Assert.NotNull(manualContext.GetHRSettings());
        Assert.NotNull(manualContext.HRConfig);
        Assert.NotNull(manualContext.GetTimesheetSettings());
        Assert.NotNull(manualContext.TimesheetConfig);
    }
}
