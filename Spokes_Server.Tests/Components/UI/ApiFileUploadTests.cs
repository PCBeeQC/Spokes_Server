using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Maintenance;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class ApiFileUploadTests : BunitContext, IDisposable
{
    private readonly string _testDataDir;
    private readonly CompanyProfileRepository _companyRepo;

    public ApiFileUploadTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ApiFileUpload_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "DataPath", _testDataDir },
            { "Spokes_DemoMode", "false" }
        }).Build();

        Services.AddSingleton<IConfiguration>(config);
        Services.AddLogging();
        Services.AddMudServices();
        Services.AddLocalization();

        var writer = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        _companyRepo = new CompanyProfileRepository(writer, config);
        Services.AddSingleton(_companyRepo);
        Services.AddScoped<StartupTimingService>();

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public new void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_testDataDir))
            {
                Directory.Delete(_testDataDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void ApiFileUpload_InitializesWithConfiguredCompanyProfileLimit()
    {
        // 2048 MB (2 GB) configured in Company Settings
        const long configuredLimit = 2_147_483_648L;
        _companyRepo.Save(new CompanyProfile { Id = "GlobalProfile", MaxFileUploadSizeBytes = configuredLimit });

        var cut = Render<ApiFileUpload>(parameters => parameters
            .Add(p => p.Category, "projectnotes")
            .Add(p => p.ContextId, "proj-123"));

        var invocation = JSInterop.Invocations
            .First(inv => inv.Identifier == "spokesUpload.initializeInput");

        Assert.Equal(5, invocation.Arguments.Count);
        // Argument 0: inputId (string)
        // Argument 1: DotNetObjectReference
        // Argument 2: maxAllowedSize (long)
        var passedSize = Assert.IsType<long>(invocation.Arguments[2]);
        Assert.Equal(configuredLimit, passedSize);
    }

    [Fact]
    public void ApiFileUpload_WhenMaxAllowedSizeParameterProvided_OverridesCompanyProfileLimit()
    {
        // 2048 MB configured in Company Settings
        _companyRepo.Save(new CompanyProfile { Id = "GlobalProfile", MaxFileUploadSizeBytes = 2_147_483_648L });

        // Component requests a 5MB restriction
        const long explicitLimit = 5 * 1024 * 1024L;
        var cut = Render<ApiFileUpload>(parameters => parameters
            .Add(p => p.Category, "avatars")
            .Add(p => p.ContextId, "emp-123")
            .Add(p => p.MaxAllowedSize, explicitLimit));

        var invocation = JSInterop.Invocations
            .First(inv => inv.Identifier == "spokesUpload.initializeInput");

        Assert.Equal(5, invocation.Arguments.Count);
        var passedSize = Assert.IsType<long>(invocation.Arguments[2]);
        Assert.Equal(explicitLimit, passedSize);
    }
}
