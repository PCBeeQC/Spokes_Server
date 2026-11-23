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
            { "DataPath", _testDataDir }, { "SPOKES_MASTER_PASSWORD", "UnitTest-Master-Password-2026!" },
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
            .First(inv => inv.Identifier == "window.spokesInitializeUploadInput");

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
            .First(inv => inv.Identifier == "window.spokesInitializeUploadInput");

        Assert.Equal(5, invocation.Arguments.Count);
        var passedSize = Assert.IsType<long>(invocation.Arguments[2]);
        Assert.Equal(explicitLimit, passedSize);
    }

    [Fact]
    public async Task ApiFileUpload_WhenShowProgressIsFalse_SkipsInternalReRendersOnCallbacks()
    {
        int startedCount = 0;
        var progressUpdates = new List<(string State, double Progress)>();

        var cut = Render<ApiFileUpload>(parameters => parameters
            .Add(p => p.Category, "chat")
            .Add(p => p.ContextId, "general")
            .Add(p => p.ShowProgress, false)
            .Add(p => p.OnUploadStarted, () => startedCount++)
            .Add(p => p.OnDetailedProgressUpdate, update => progressUpdates.Add(update)));

        int initialRenderCount = cut.RenderCount;

        await cut.InvokeAsync(async () =>
        {
            await cut.Instance.OnUploadStartedCallback(1);
            await cut.Instance.OnCompressingCallback(System.Text.Json.JsonDocument.Parse("-1").RootElement);
            await cut.Instance.OnProgressUpdateCallback(System.Text.Json.JsonDocument.Parse("50").RootElement);
            await cut.Instance.OnProcessingCallback("Processing...");
            await cut.Instance.OnUploadErrorCallback("Cancelled");
        });

        Assert.Equal(1, startedCount);
        Assert.Equal(3, progressUpdates.Count);
        Assert.Equal(initialRenderCount, cut.RenderCount);
    }

    [Fact]
    public async Task ApiFileUpload_WhenShowProgressIsTrue_ReRendersOnProgressCallbacks()
    {
        var cut = Render<ApiFileUpload>(parameters => parameters
            .Add(p => p.Category, "chat")
            .Add(p => p.ContextId, "general")
            .Add(p => p.ShowProgress, true));

        int initialRenderCount = cut.RenderCount;

        await cut.InvokeAsync(async () =>
        {
            await cut.Instance.OnUploadStartedCallback(1);
            await cut.Instance.OnProgressUpdateCallback(System.Text.Json.JsonDocument.Parse("60").RootElement);
        });

        Assert.True(cut.RenderCount > initialRenderCount);
        Assert.Contains("60%", cut.Markup);
    }
}
